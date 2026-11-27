using System.Text.Json;
using GangioKeet.Client.Api;
using GangioKeet.Inference;
using GangioKeet.Inference.Cleaning;
using Xunit;

namespace GangioKeet.Client.Tests.Api;

public sealed class DictationContractTests
{
    [Fact]
    public void AcceptsOnlyTheConfiguredAlias()
    {
        Assert.Null(DictationContract.ValidateTranscription("gangio-keet",
            ["model", "response_format"], key => key == "model" ? "gangio-keet" : "json"));
        ApiError changed = DictationContract.ValidateTranscription("gangio-keet",
            ["model"], _ => "whisper-1")!;
        Assert.Equal(400, changed.Status);
        Assert.Equal("model_not_found", changed.Error);
    }

    [Theory]
    [InlineData("language", "it")]
    [InlineData("prompt", "ciao")]
    [InlineData("temperature", "0")]
    public void RefusesParametersThatWouldChangeBehaviour(string key, string value)
    {
        ApiError error = DictationContract.ValidateTranscription("gangio-keet",
            ["model", key], field => field == "model" ? "gangio-keet" : value)!;
        Assert.Equal(400, error.Status);
        Assert.Contains(key, error.Message);
    }

    [Theory]
    [InlineData(null, "default")]
    [InlineData("", "default")]
    [InlineData("code", "code")]
    [InlineData("terminal", "terminal")]
    public void MapsOnlyKnownProfiles(string? given, string expected) =>
        Assert.Equal(expected, DictationContract.ProfileFrom(given));

    [Fact]
    public void RejectsAnInventedProfile() =>
        Assert.Throws<ArgumentException>(() => DictationContract.ProfileFrom("hacker"));

    [Theory]
    [InlineData("default")]
    [InlineData("code")]
    [InlineData("terminal")]
    [InlineData("chat")]
    [InlineData("email")]
    [InlineData("document")]
    public void EveryProfileHereIsAlsoAProfileTheCleanerAccepts(string profile)
    {
        // Le due liste non possono divergere in silenzio: un profilo che il server
        // accetta ma il cleaner rifiuta diventerebbe un 500 al momento della pulizia.
        Assert.Equal(profile, DictationContract.ProfileFrom(profile));
        string rendered = CleanerPrompt.Render("prova", profile, new Dictionary<string, string>());
        Assert.Contains($"<profile>{profile}</profile>", rendered);
    }

    [Fact]
    public void EmptyTextOnMutedAudioIsMarkedNoSpeech()
    {
        Assert.Equal("{\"text\":\"\"}", DictationContract.ToJson("", noSpeech: false));
        Assert.Equal("{\"text\":\"\",\"no_speech\":true}", DictationContract.ToJson("", noSpeech: true));
    }

    // I test che seguono non sono nel brief: pinnano le forme JSON e i codici
    // d'errore che il brief descrive a parole ma non verifica.
    [Fact]
    public void FileAndResponseFormatAreNotBehaviourParameters()
    {
        Assert.Null(DictationContract.ValidateTranscription("gangio-keet",
            ["file", "model", "response_format"],
            key => key switch
            {
                "model" => "gangio-keet",
                "response_format" => "text",
                _ => "dictation.wav"
            }));
    }

    [Fact]
    public void RefusesAnUnknownResponseFormat()
    {
        ApiError error = DictationContract.ValidateTranscription("gangio-keet",
            ["response_format"], _ => "verbose_json")!;
        Assert.Equal(400, error.Status);
        Assert.Equal("unsupported_parameter", error.Error);
        Assert.Contains("response_format", error.Message);
    }

    [Fact]
    public void DictationAcceptsOnlyItsOwnFields()
    {
        // `response_format` è dell'altra rotta, `profile` è di questa: due insiemi diversi
        // sulla stessa regola, così un campo ignoto non passa da nessuna delle due.
        Assert.Null(DictationContract.ValidateDictation("gangio-keet",
            ["file", "model", "profile"], key => key switch
            {
                "model" => "gangio-keet",
                "profile" => "code",
                _ => "dictation.wav"
            }));

        ApiError format = DictationContract.ValidateDictation("gangio-keet",
            ["response_format"], _ => "text")!;
        Assert.Equal("unsupported_parameter", format.Error);
    }

    [Fact]
    public void DictationRejectsAnUnknownModelAndLanguage()
    {
        ApiError model = DictationContract.ValidateDictation("gangio-keet",
            ["model"], _ => "whisper-1")!;
        Assert.Equal("model_not_found", model.Error);
        Assert.Equal(400, model.Status);

        ApiError language = DictationContract.ValidateDictation("gangio-keet",
            ["language"], _ => "it")!;
        Assert.Equal("unsupported_parameter", language.Error);
        Assert.Contains("language", language.Message);
    }

    [Fact]
    public void RefusedParameterCarriesItsOwnCode()
    {
        ApiError error = DictationContract.ValidateTranscription("gangio-keet",
            ["language"], _ => "it")!;
        Assert.Equal("unsupported_parameter", error.Error);
        Assert.Equal(400, error.Status);
    }

    [Fact]
    public void UnsupportedProfileFactoryKeepsItsOwnCode()
    {
        ApiError error = ApiError.UnsupportedProfile("profilo 'hacker' non ammesso");
        Assert.Equal("unsupported_profile", error.Error);
        Assert.Equal(400, error.Status);
        // La factory non deve confondersi con quella dei parametri: sono due codici
        // distinti, e Task 4 li serve entrambi come 400.
        Assert.NotEqual(ApiError.BadRequest(ApiError.UnsupportedParameterCode, "x").Error, error.Error);
    }

    [Fact]
    public void DictationInputCarriesThePcmAndTheProfile()
    {
        DictationInput input = new(new byte[4], "code");
        Assert.Equal(4, input.Pcm16.Length);
        Assert.Equal("code", input.Profile);
    }

    [Fact]
    public void PlainTextIsOnlyWhenTheCallerAskedForIt()
    {
        Assert.True(DictationContract.WantsPlainText(
            ["model", "response_format"], key => key == "response_format" ? "text" : "gangio-keet"));
        Assert.False(DictationContract.WantsPlainText(
            ["model", "response_format"], key => key == "response_format" ? "json" : "gangio-keet"));
        Assert.False(DictationContract.WantsPlainText(["model"], _ => "gangio-keet"));
    }

    [Fact]
    public void OmittingTheModelIsAllowed()
    {
        // C'è un solo modello: un client che non manda `model` non sta scegliendo niente.
        Assert.Null(DictationContract.ValidateTranscription("gangio-keet",
            ["file", "response_format"], _ => "json"));
    }

    [Fact]
    public void ModelNotFoundMessageNamesBothModels()
    {
        ApiError error = ApiError.ModelNotFound("whisper-1", "gangio-keet");
        Assert.Equal(400, error.Status);
        Assert.Equal("model_not_found", error.Error);
        Assert.Contains("whisper-1", error.Message);
        Assert.Contains("gangio-keet", error.Message);
    }

    [Fact]
    public void DictationJsonMatchesTheDocumentedShape()
    {
        PipelineResult result = new("ehm ciao", "Ciao.", 100, 20, 120, [], Stt, null, null);

        Assert.Equal(
            "{\"text\":\"Ciao.\",\"raw_text\":\"ehm ciao\",\"cleaner_text\":null,"
            + "\"no_speech\":false,\"warnings\":[],"
            + "\"latency\":{\"stt_ms\":100,\"llm_ms\":20,\"total_ms\":120}}",
            DictationContract.ToDictationJson(result));
    }

    [Fact]
    public void DictationJsonKeepsTheCleanedTextAndTheWarnings()
    {
        PipelineResult result = new("ehm ciao", "Ciao.", 100, 20, 120,
            ["llm_timeout"], Stt, Stt, "Ciao.");

        string json = DictationContract.ToDictationJson(result);

        Assert.Contains("\"cleaner_text\":\"Ciao.\"", json);
        Assert.Contains("\"warnings\":[\"llm_timeout\"]", json);
    }

    [Fact]
    public void MutedAudioIsNoSpeechInTheFullJson()
    {
        PipelineResult result = new("", "", 100, 0, 100, [], Stt, null, null);

        Assert.Equal(
            "{\"text\":\"\",\"raw_text\":\"\",\"cleaner_text\":null,"
            + "\"no_speech\":true,\"warnings\":[],"
            + "\"latency\":{\"stt_ms\":100,\"llm_ms\":0,\"total_ms\":100}}",
            DictationContract.ToDictationJson(result));
    }

    [Fact]
    public void TextThatWouldBreakJsonIsEscaped()
    {
        // Le virgolette e i caratteri di controllo non possono rompere il documento.
        // Utf8JsonWriter le scrive come \u0022 e \n: JSON valido per qualunque parser.
        Assert.Equal("{\"text\":\"a\\u0022b\\nc\"}", DictationContract.ToJson("a\"b\nc", noSpeech: false));
    }

    [Fact]
    public void TextFromAudioCannotBecomeMarkup()
    {
        // Il testo viene da un audio caricato dal chiamante: non deve passare per
        // markup se un client lo mette in una pagina.
        Assert.Equal("{\"text\":\"\\u003Cb\\u003E \\u0026\"}",
            DictationContract.ToJson("<b> &", noSpeech: false));
    }

    [Fact]
    public void AccentsSurviveTheRoundTrip()
    {
        // L'italiano è la lingua normale di questa app: "perché" deve tornare uguale,
        // comunque sia scritto nel JSON.
        using System.Text.Json.JsonDocument document =
            System.Text.Json.JsonDocument.Parse(DictationContract.ToJson("perché", noSpeech: false));
        Assert.Equal("perché", document.RootElement.GetProperty("text").GetString());
    }

    // La rotta di correzione testo→testo: il corpo e la risposta sono definiti qui, così
    // la regola si prova senza aprire una porta.

    [Fact]
    public void CorrectJsonIsExactlyTheText()
    {
        // Nessun `no_speech`, nessun `raw_text`: per un testo non c'è audio da cui dedurre
        // niente, e un campo in più sarebbe una promessa che la rotta non può mantenere.
        Assert.Equal("{\"text\":\"\"}", DictationContract.ToCorrectJson(""));
        Assert.Equal("{\"text\":\"Ciao.\"}", DictationContract.ToCorrectJson("Ciao."));
    }

    [Fact]
    public void CorrectFromReadsTheTextAndDefaultsTheProfile()
    {
        Assert.Null(DictationContract.CorrectFrom("""{"text":" ehm ciao "}""", out CorrectRequest? request));
        Assert.NotNull(request);
        // Il testo arriva intatto: a togliere gli spazi ci pensa LightCleanup, come nella
        // pipeline locale.
        Assert.Equal(" ehm ciao ", request!.Text);
        Assert.Equal("default", request.Profile);
    }

    [Fact]
    public void CorrectFromKeepsTheProfileItWasGiven()
    {
        Assert.Null(DictationContract.CorrectFrom("""{"text":"x","profile":"code"}""",
            out CorrectRequest? request));
        Assert.Equal("code", request!.Profile);
    }

    [Fact]
    public void CorrectFromRejectsWhatIsNotACorrectRequest()
    {
        // Ogni caso è colpa del chiamante e vale lo stesso codice: un 500 direbbe che il
        // server si è rotto, mentre qui l'unica cosa rotta è la forma del corpo.
        Assert.Equal("invalid_request", DictationContract.CorrectFrom(null, out _)!.Error);
        Assert.Equal("invalid_request", DictationContract.CorrectFrom("", out _)!.Error);
        Assert.Equal("invalid_request", DictationContract.CorrectFrom("non json", out _)!.Error);
        Assert.Equal("invalid_request", DictationContract.CorrectFrom("[]", out _)!.Error);
        Assert.Equal("invalid_request", DictationContract.CorrectFrom("{}", out _)!.Error);
        Assert.Equal("invalid_request", DictationContract.CorrectFrom("""{"text":5}""", out _)!.Error);
        // Un campo che la rotta non conosce non va ignorato in silenzio: il chiamante
        // crederebbe di aver controllato qualcosa.
        ApiError unknown = DictationContract.CorrectFrom(
            """{"text":"x","model":"gangio-keet"}""", out _)!;
        Assert.Equal("invalid_request", unknown.Error);
        Assert.Contains("model", unknown.Message);
    }

    [Fact]
    public void CorrectFromRejectsAnUnknownProfile()
    {
        ApiError error = DictationContract.CorrectFrom("""{"text":"x","profile":"hacker"}""", out _)!;
        Assert.Equal("unsupported_profile", error.Error);
        Assert.Equal(400, error.Status);
    }

    [Fact]
    public void CorrectFromRejectsMoreThanOneHundredThousandCharacters()
    {
        Assert.Null(DictationContract.CorrectFrom(
            JsonSerializer.Serialize(new { text = new string('a', 100_000) }), out _));
        Assert.Equal("invalid_request", DictationContract.CorrectFrom(
            JsonSerializer.Serialize(new { text = new string('a', 100_001) }), out _)!.Error);
    }

    private static EngineIdentity Stt => new("parakeet", "rev", "sha", "1", "cpu", "fp16", "host");
}
