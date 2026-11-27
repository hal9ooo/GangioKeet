param(
    [ValidateSet('Add', 'Remove', 'Status')][string]$Action = 'Status',
    [ValidateRange(1024, 65535)][int]$Port = 5099,
    # The rule may only ever be scoped to the tailnet CGNAT range. Accepting another
    # value would turn a Tailscale-only API into an API open to the whole LAN.
    [ValidateSet('100.64.0.0/10')][string]$RemoteAddress = '100.64.0.0/10'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Same name for all three actions: Add refuses to stack a second rule, Remove finds the
# one Add created, Status never needs a rule id the operator has to copy around.
$RuleName = 'ChattyNeko API (Tailscale)'
$TailscaleAlias = 'Tailscale'

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

# IPv4 addresses of the Tailscale interface. Empty means the tunnel is not up (or
# Tailscale is not installed): an inbound rule for an interface that does not exist
# would silently allow nothing, so Add refuses instead of writing a dead rule.
function Get-TailscaleAddress {
    return @(Get-NetIPAddress -InterfaceAlias $TailscaleAlias -AddressFamily IPv4 -ErrorAction SilentlyContinue)
}

function Get-ChattyRule {
    return @(Get-NetFirewallRule -DisplayName $RuleName -ErrorAction SilentlyContinue)
}

# True when the rule already has the port, the protocol and the tailnet scope that this
# script would write. Anything else is stale (for example a rule created by hand for
# another port) and Add replaces it rather than leaving two rules that disagree.
#
# The scope test is equality, not containment. `-contains` would be true for a
# hand-edited rule carrying 100.64.0.0/10 *plus* another scope (even `Any`): Add would
# call it correct and leave a rule wider than the tailnet, which is exactly what the doc
# promises never happens. A rule is only right when the only remote address it allows is
# the tailnet one, so the count must be 1 and the value must match.
function Test-RuleMatches([object]$Rule) {
    $portFilter = $Rule | Get-NetFirewallPortFilter
    $addressFilter = $Rule | Get-NetFirewallAddressFilter
    $remoteAddresses = @($addressFilter.RemoteAddress)
    return $Rule.Enabled -eq 'True' -and
        $Rule.Direction -eq 'Inbound' -and
        $Rule.Action -eq 'Allow' -and
        $portFilter.Protocol -eq 'TCP' -and
        "$($portFilter.LocalPort)" -eq "$Port" -and
        $remoteAddresses.Count -eq 1 -and
        "$($remoteAddresses[0])" -eq $RemoteAddress
}

function Show-Rule([object]$Rule) {
    $portFilter = $Rule | Get-NetFirewallPortFilter
    $addressFilter = $Rule | Get-NetFirewallAddressFilter
    Write-Output ("  DisplayName : {0}" -f $Rule.DisplayName)
    Write-Output ("  Enabled     : {0}" -f $Rule.Enabled)
    Write-Output ("  Direction   : {0}" -f $Rule.Direction)
    Write-Output ("  Action      : {0}" -f $Rule.Action)
    Write-Output ("  Protocol    : {0}" -f $portFilter.Protocol)
    Write-Output ("  LocalPort   : {0}" -f ($portFilter.LocalPort -join ', '))
    Write-Output ("  RemoteAddress: {0}" -f (@($addressFilter.RemoteAddress) -join ', '))
}

if (-not (Test-IsAdministrator)) {
    # Status only reads rules; Add and Remove write them and need an elevated process.
    if ($Action -eq 'Status') {
        Write-Output 'Not running as administrator: showing rules read-only.'
    } else {
        Write-Output "Administrator rights are required for '$Action': requesting elevation..."
        $arguments = @(
            '-NoProfile', '-ExecutionPolicy', 'Bypass',
            '-File', ('"{0}"' -f $PSCommandPath),
            '-Action', $Action,
            '-Port', $Port,
            '-RemoteAddress', $RemoteAddress
        )
        try {
            $elevated = Start-Process -FilePath (Get-Process -Id $PID).Path -Verb RunAs `
                -ArgumentList $arguments -Wait -PassThru
        } catch {
            Write-Error "Elevation was refused: $($_.Exception.Message)" -ErrorAction Continue
            exit 3
        }

        Write-Output "Elevated run finished with exit code $($elevated.ExitCode)."
        exit $elevated.ExitCode
    }
}

switch ($Action) {
    'Add' {
        $tailscale = @(Get-TailscaleAddress)
        if ($tailscale.Count -eq 0) {
            $message = ('Refusing to add the rule: the ''{0}'' interface has no IPv4 address. ' +
                'Start Tailscale and try again.') -f $TailscaleAlias
            Write-Error $message -ErrorAction Continue
            exit 2
        }
        Write-Output ("Tailscale interface: {0}" -f (@($tailscale | ForEach-Object { $_.IPAddress }) -join ', '))

        $existing = @(Get-ChattyRule)
        if ($existing.Count -eq 1 -and (Test-RuleMatches $existing[0])) {
            Write-Output "Rule '$RuleName' is already present and matches; nothing to do."
            Show-Rule $existing[0]
            exit 0
        }
        if ($existing.Count -gt 0) {
            # A rule with the same name but a different port or scope would let the old
            # port open: it is replaced, never left next to the new one.
            Write-Output "Replacing the existing rule '$RuleName' (port or scope differs)..."
            $existing | Remove-NetFirewallRule
        }

        New-NetFirewallRule -DisplayName $RuleName `
            -Description 'ChattyNeko OpenAI-compatible API; reachable only from the Tailscale tailnet.' `
            -Direction Inbound -Protocol TCP -LocalPort $Port `
            -RemoteAddress $RemoteAddress -Action Allow -Profile Any | Out-Null

        Write-Output "Added rule '$RuleName': TCP port $Port open only to $RemoteAddress."
        $added = @(Get-ChattyRule)
        Show-Rule $added[0]
        exit 0
    }
    'Remove' {
        $existing = @(Get-ChattyRule)
        if ($existing.Count -eq 0) {
            Write-Output "Rule '$RuleName' is not present; nothing to remove."
            exit 0
        }
        $existing | Remove-NetFirewallRule
        Write-Output "Removed rule '$RuleName' ($($existing.Count) entr$(if ($existing.Count -eq 1) { 'y' } else { 'ies' }))."
        exit 0
    }
    'Status' {
        $existing = @(Get-ChattyRule)
        if ($existing.Count -eq 0) {
            Write-Output "Rule '$RuleName' is not present."
        } else {
            Write-Output "Rule '$RuleName':"
            foreach ($rule in $existing) { Show-Rule $rule }
        }

        $tailscale = @(Get-TailscaleAddress)
        if ($tailscale.Count -eq 0) {
            Write-Output "Tailscale interface: not found (the rule cannot match any traffic)."
        } else {
            Write-Output ("Tailscale interface: {0}" -f (@($tailscale | ForEach-Object { $_.IPAddress }) -join ', '))
        }
        exit 0
    }
}
