using System.Windows;
using GangioKeet.Client.Ui;
using Xunit;

namespace GangioKeet.Client.Tests.Ui;

/// <summary>
/// Lo splash è la prima finestra creata all'avvio: se il XAML non si carica,
/// l'eccezione sfugge a <c>OnStartup</c> e il processo muore senza log né
/// finestra. Il test costruisce la finestra su un thread STA per intercettare
/// gli errori di coercizione che solo il runtime WPF rileva (per esempio un
/// <c>RenderTransform</c> non valido su <c>Window</c>).
/// </summary>
public sealed class SplashWindowTests
{
    [Fact]
    public void Constructor_LoadsXamlWithoutThrowing()
    {
        Exception? failure = RunOnStaThread(() =>
        {
            // L'URI della cover è relativo al file XAML, così si risolve
            // nell'assembly del client e non in quello di entry (test host).
            SplashWindow window = new(TimeSpan.FromSeconds(3));
            window.Close();
        });

        Assert.Null(failure);
    }

    [Fact]
    public void Payoff_MentionsLocalGpusAndUnemployedMacs()
    {
        Assert.Equal("A one-user project putting unemployed Macs and local GPUs to work.", SplashWindow.Payoff);
    }

    private static Exception? RunOnStaThread(Action action)
    {
        Exception? captured = null;

        Thread thread = new(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                captured = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        return captured;
    }
}
