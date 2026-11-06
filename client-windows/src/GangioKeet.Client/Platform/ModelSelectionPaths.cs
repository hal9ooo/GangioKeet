using System.IO;
using GangioKeet.Client.Configuration;
using GangioKeet.Inference;

namespace GangioKeet.Client.Platform;

public static class ModelSelectionPaths
{
    public static string UserSelectionPath =>
        Path.Combine(Path.GetDirectoryName(ConfigLoader.DefaultPath)!, "selection.json");

    public static string InstallModelsRoot => Path.Combine(AppContext.BaseDirectory, "models");

// I metadati (catalogo, integrita, selezione) vivono fuori da models/: quella cartella
// puo essere una junction verso un altro volume, e l'installer non puo scriverci dentro.
public static string InstallCatalogRoot => Path.Combine(AppContext.BaseDirectory, "catalog");

    public static string Prepare(ClientConfig config, string? userSelectionPath = null,
        string? installModelsRoot = null)
    {
        string user = userSelectionPath ?? UserSelectionPath;
        string install = installModelsRoot ?? InstallModelsRoot;
        string selected = config.Inference.ModelSelectionPath;

        if (File.Exists(user))
        {
            config.Inference.ModelSelectionPath = user;
            return user;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(user)!);
        if (File.Exists(selected))
        {
            ModelSelection selection = ModelSelectionFile.Load(selected);
            if (selection.SchemaVersion == 1) ModelSelectionFile.Migrate(selected, user);
            else File.Copy(selected, user, overwrite: false);
        }
        else
        {
            string seed = Path.Combine(install, "selection.json");
            if (!File.Exists(seed))
                throw new FileNotFoundException("Model selection seed not found.", seed);
            File.Copy(seed, user, overwrite: false);
        }

        config.Inference.ModelSelectionPath = user;
        return user;
    }
}
