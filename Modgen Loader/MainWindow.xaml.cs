using InfinityModgenManager.Models;
using InfinityModgenManager.Services;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace InfinityModgenManager
{
    public partial class MainWindow : Window
    {
        private readonly ObservableCollection<Game> games =
            new ObservableCollection<Game>();

        private readonly ObservableCollection<Mod> mods =
            new ObservableCollection<Mod>();

        private readonly PakManager pakManager =
            new PakManager();

        private readonly PakInspector pakInspector =
            new PakInspector();

        private readonly RuntimeDeploymentService runtimeDeploymentService =
            new RuntimeDeploymentService();

        private Game? selectedGame;

        private string GamesConfigDirectory
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(
                        Environment.SpecialFolder.ApplicationData),
                    "Infinity Modgen Manager");
            }
        }

        private string GamesConfigPath
        {
            get
            {
                return Path.Combine(
                    GamesConfigDirectory,
                    "games.json");
            }
        }

        public MainWindow()
        {
            InitializeComponent();

            RegisterGameBananaProtocol();

            GameSelector.ItemsSource = games;
            ModsList.ItemsSource = mods;

            LoadGames();

            if (IsGameBananaStartup())
            {
                Hide();
            }

            _ = HandleStartupArgumentsAsync();
        }

        private bool IsGameBananaStartup()
        {
            string[] arguments =
                Environment.GetCommandLineArgs();

            if (arguments.Length < 2)
                return false;

            string protocolArgument =
                arguments[1];

            return protocolArgument.StartsWith(
                "infinitymodgen:",
                StringComparison.OrdinalIgnoreCase);
        }

        private void RegisterGameBananaProtocol()
        {
            try
            {
                using RegistryKey? protocolKey =
                    Registry.CurrentUser.CreateSubKey(
                        @"Software\Classes\infinitymodgen");

                if (protocolKey == null)
                    return;

                protocolKey.SetValue(
                    "",
                    "URL:Infinity Modgen Protocol");

                protocolKey.SetValue(
                    "URL Protocol",
                    "");

                using RegistryKey? commandKey =
                    protocolKey.CreateSubKey(
                        @"shell\open\command");

                if (commandKey == null)
                    return;

                string applicationPath =
                    Environment.ProcessPath ?? "";

                if (string.IsNullOrWhiteSpace(
                    applicationPath))
                {
                    return;
                }

                commandKey.SetValue(
                    "",
                    "\"" +
                    applicationPath +
                    "\" \"%1\"");
            }
            catch
            {
                // Protocol registration failure should not
                // prevent the Manager from starting.
            }
        }

        private async Task HandleStartupArgumentsAsync()
        {
            try
            {
                string[] arguments =
                    Environment.GetCommandLineArgs();

                if (arguments.Length < 2)
                    return;

                string protocolArgument =
                    arguments[1];

                if (string.IsNullOrWhiteSpace(
                    protocolArgument))
                {
                    return;
                }

                if (!protocolArgument.StartsWith(
                    "infinitymodgen:",
                    StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                await InstallGameBananaProtocolModAsync(
                    protocolArgument);

                Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "The GameBanana mod could not be installed.\n\n" +
                    ex.Message,
                    "Infinity Modgen Manager",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);

                if (IsGameBananaStartup())
                {
                    Application.Current.Shutdown();
                }
            }
        }

        private async Task InstallGameBananaProtocolModAsync(
            string protocolArgument)
        {
            string payload =
                protocolArgument.Substring(
                    "infinitymodgen:".Length);

            if (string.IsNullOrWhiteSpace(
                payload))
            {
                throw new InvalidOperationException(
                    "The GameBanana 1-Click URL did not contain a download URL.");
            }

            string[] parts =
                payload.Split(
                    ',',
                    StringSplitOptions.None);

            string downloadUrl =
                parts[0].Trim();

            if (!Uri.TryCreate(
                downloadUrl,
                UriKind.Absolute,
                out Uri? downloadUri))
            {
                throw new InvalidOperationException(
                    "The GameBanana download URL is invalid.");
            }

            if (!string.Equals(
                downloadUri.Scheme,
                "https",
                StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(
                downloadUri.Scheme,
                "http",
                StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "The GameBanana download URL must use HTTP or HTTPS.");
            }

            string? gameBananaItemType =
                null;

            string? gameBananaModId =
                null;

            foreach (string rawPart in parts.Skip(1))
            {
                string part =
                    rawPart.Trim();

                if (string.IsNullOrWhiteSpace(
                    part))
                {
                    continue;
                }

                int separatorIndex =
                    part.IndexOf(':');

                if (separatorIndex > 0)
                {
                    string key =
                        part.Substring(
                            0,
                            separatorIndex)
                        .Trim();

                    string value =
                        part.Substring(
                            separatorIndex + 1)
                        .Trim();

                    try
                    {
                        value =
                            Uri.UnescapeDataString(
                                value);
                    }
                    catch
                    {
                        // Keep the original value if decoding fails.
                    }

                    if (string.Equals(
                        key,
                        "gb_itemtype",
                        StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(
                        key,
                        "itemtype",
                        StringComparison.OrdinalIgnoreCase))
                    {
                        gameBananaItemType =
                            value;

                        continue;
                    }

                    if (string.Equals(
                        key,
                        "gb_itemid",
                        StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(
                        key,
                        "itemid",
                        StringComparison.OrdinalIgnoreCase))
                    {
                        gameBananaModId =
                            value;

                        continue;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(
                gameBananaItemType) &&
                parts.Length >= 2)
            {
                string positionalType =
                    parts[1].Trim();

                if (!string.IsNullOrWhiteSpace(
                    positionalType) &&
                    !positionalType.Contains(
                        ":",
                        StringComparison.Ordinal))
                {
                    gameBananaItemType =
                        positionalType;
                }
            }

            if (string.IsNullOrWhiteSpace(
                gameBananaModId) &&
                parts.Length >= 3)
            {
                string positionalId =
                    parts[2].Trim();

                if (!string.IsNullOrWhiteSpace(
                    positionalId) &&
                    !positionalId.Contains(
                        ":",
                        StringComparison.Ordinal))
                {
                    gameBananaModId =
                        positionalId;
                }
            }

            if (string.IsNullOrWhiteSpace(
                gameBananaItemType))
            {
                gameBananaItemType =
                    "Mod";
            }

            if (!string.Equals(
                gameBananaItemType,
                "Mod",
                StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "The GameBanana 1-Click URL does not contain a supported Mod item type.");
            }

            if (string.IsNullOrWhiteSpace(
                gameBananaModId))
            {
                throw new InvalidOperationException(
                    "The GameBanana 1-Click URL did not contain a GameBanana Mod ID.");
            }

            string? gameBananaGameName =
                await GetGameBananaGameNameAsync(
                    gameBananaItemType,
                    gameBananaModId);

            if (string.IsNullOrWhiteSpace(
                gameBananaGameName))
            {
                throw new InvalidOperationException(
                    "GameBanana did not return a game name for Mod " +
                    gameBananaModId +
                    ".");
            }

            Game? matchingGame =
                FindMatchingGame(
                    gameBananaGameName);

            if (matchingGame == null)
            {
                matchingGame =
                    games.FirstOrDefault(
                        game =>
                            string.Equals(
                                game.Preset,
                                gameBananaGameName,
                                StringComparison.OrdinalIgnoreCase));
            }

            if (matchingGame == null)
            {
                string configuredGames =
                    games.Count == 0
                        ? "No games are currently configured."
                        : string.Join(
                            "\n",
                            games.Select(
                                game =>
                                    "• " +
                                    game.Name));

                throw new InvalidOperationException(
                    "GameBanana reports that this mod belongs to:\n\n" +
                    gameBananaGameName +
                    "\n\n" +
                    "Infinity Modgen Manager could not find that game " +
                    "in your configured games.\n\n" +
                    "Configured games:\n" +
                    configuredGames +
                    "\n\n" +
                    "Add the game to Infinity Modgen Manager first.");
            }

            selectedGame =
                matchingGame;

            GameSelector.SelectedItem =
                matchingGame;

            ScanMods(
                matchingGame);

            UpdateSettingsPage();

            GameBananaModMetadata metadata =
                await GetGameBananaModMetadataAsync(
                    gameBananaItemType,
                    gameBananaModId);

            string modName =
                string.IsNullOrWhiteSpace(
                    metadata.Name)
                    ? "GameBanana Mod " +
                      gameBananaModId
                    : metadata.Name;

            string author =
                string.IsNullOrWhiteSpace(
                    metadata.Author)
                    ? "Unknown"
                    : metadata.Author;

            string description =
                string.IsNullOrWhiteSpace(
                    metadata.Description)
                    ? "No description available."
                    : metadata.Description;

            GameBananaInstallWindow installWindow =
                new GameBananaInstallWindow(
                    matchingGame,
                    downloadUri.ToString(),
                    gameBananaModId,
                    modName,
                    author,
                    description,
                    "Unknown",
                    null);

            bool? installationResult =
                installWindow.ShowDialog();

            if (installationResult != true)
            {
                throw new InvalidOperationException(
                    "The GameBanana installation was cancelled.");
            }

            SaveGames();

            RefreshMods();

            GameSelector.SelectedItem =
                matchingGame;
        }

        private sealed class GameBananaModMetadata
        {
            public string? Name { get; set; }

            public string? Author { get; set; }

            public string? Description { get; set; }
        }

        private async Task<GameBananaModMetadata>
            GetGameBananaModMetadataAsync(
                string itemType,
                string itemId)
        {
            GameBananaModMetadata metadata =
                new GameBananaModMetadata();

            if (!string.Equals(
                itemType,
                "Mod",
                StringComparison.OrdinalIgnoreCase))
            {
                return metadata;
            }

            if (!int.TryParse(
                itemId,
                out int parsedItemId))
            {
                return metadata;
            }

            string apiUrl =
                "https://api.gamebanana.com/Core/Item/Data" +
                "?itemtype=Mod" +
                "&itemid=" +
                parsedItemId +
                "&fields=name,description,authors" +
                "&return_keys=1" +
                "&format=json";

            try
            {
                using HttpClient client =
                    new HttpClient();

                client.Timeout =
                    TimeSpan.FromSeconds(30);

                client.DefaultRequestHeaders.UserAgent.ParseAdd(
                    "Infinity Modgen Manager");

                using HttpResponseMessage response =
                    await client.GetAsync(
                        apiUrl);

                response.EnsureSuccessStatusCode();

                string json =
                    await response.Content.ReadAsStringAsync();

                using JsonDocument document =
                    JsonDocument.Parse(json);

                JsonElement root =
                    document.RootElement;

                JsonElement metadataObject =
                    root;

                if (root.ValueKind ==
                    JsonValueKind.Array &&
                    root.GetArrayLength() > 0)
                {
                    metadataObject =
                        root[0];
                }

                if (metadataObject.ValueKind ==
                    JsonValueKind.Object)
                {
                    if (metadataObject.TryGetProperty(
                        "name",
                        out JsonElement nameElement))
                    {
                        metadata.Name =
                            ExtractJsonString(
                                nameElement);
                    }

                    if (metadataObject.TryGetProperty(
                        "description",
                        out JsonElement descriptionElement))
                    {
                        metadata.Description =
                            ExtractJsonString(
                                descriptionElement);
                    }

                    if (metadataObject.TryGetProperty(
                        "authors",
                        out JsonElement authorsElement))
                    {
                        metadata.Author =
                            ExtractAuthorName(
                                authorsElement);
                    }
                }
            }
            catch
            {
                /*
                 * Metadata is optional.
                 *
                 * Installation should still work if
                 * GameBanana's metadata request fails.
                 */
            }

            if (string.IsNullOrWhiteSpace(
                    metadata.Name) ||
                string.IsNullOrWhiteSpace(
                    metadata.Description) ||
                string.IsNullOrWhiteSpace(
                    metadata.Author))
            {
                try
                {
                    string fallbackUrl =
                        "https://api.gamebanana.com/Core/Item/Data" +
                        "?itemtype=Mod" +
                        "&itemid=" +
                        parsedItemId +
                        "&fields=name,description,authors" +
                        "&format=json";

                    using HttpClient client =
                        new HttpClient();

                    client.Timeout =
                        TimeSpan.FromSeconds(30);

                    client.DefaultRequestHeaders.UserAgent.ParseAdd(
                        "Infinity Modgen Manager");

                    using HttpResponseMessage response =
                        await client.GetAsync(
                            fallbackUrl);

                    response.EnsureSuccessStatusCode();

                    string json =
                        await response.Content.ReadAsStringAsync();

                    using JsonDocument document =
                        JsonDocument.Parse(json);

                    JsonElement root =
                        document.RootElement;

                    if (root.ValueKind ==
                        JsonValueKind.Array)
                    {
                        if (root.GetArrayLength() >= 1 &&
                            string.IsNullOrWhiteSpace(
                                metadata.Name))
                        {
                            metadata.Name =
                                ExtractJsonString(
                                    root[0]);
                        }

                        if (root.GetArrayLength() >= 2 &&
                            string.IsNullOrWhiteSpace(
                                metadata.Description))
                        {
                            metadata.Description =
                                ExtractJsonString(
                                    root[1]);
                        }

                        if (root.GetArrayLength() >= 3 &&
                            string.IsNullOrWhiteSpace(
                                metadata.Author))
                        {
                            metadata.Author =
                                ExtractAuthorName(
                                    root[2]);
                        }
                    }
                }
                catch
                {
                    // Keep whatever metadata was successfully retrieved.
                }
            }

            return metadata;
        }

        private static string? ExtractJsonString(
            JsonElement element)
        {
            if (element.ValueKind ==
                JsonValueKind.String)
            {
                return element.GetString();
            }

            return null;
        }

        private static string? ExtractAuthorName(
            JsonElement element)
        {
            if (element.ValueKind ==
                JsonValueKind.String)
            {
                return element.GetString();
            }

            if (element.ValueKind ==
                JsonValueKind.Array)
            {
                foreach (JsonElement author in element.EnumerateArray())
                {
                    string? name =
                        ExtractAuthorName(
                            author);

                    if (!string.IsNullOrWhiteSpace(
                        name))
                    {
                        return name;
                    }
                }

                return null;
            }

            if (element.ValueKind ==
                JsonValueKind.Object)
            {
                if (element.TryGetProperty(
                    "name",
                    out JsonElement nameElement))
                {
                    return ExtractJsonString(
                        nameElement);
                }

                if (element.TryGetProperty(
                    "username",
                    out JsonElement usernameElement))
                {
                    return ExtractJsonString(
                        usernameElement);
                }

                if (element.TryGetProperty(
                    "authors",
                    out JsonElement authorsElement))
                {
                    return ExtractAuthorName(
                        authorsElement);
                }
            }

            return null;
        }

        private async Task<string?> GetGameBananaGameNameAsync(
            string itemType,
            string itemId)
        {
            if (!string.Equals(
                itemType,
                "Mod",
                StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (!int.TryParse(
                itemId,
                out int parsedItemId))
            {
                throw new InvalidOperationException(
                    "The GameBanana Mod ID is invalid: " +
                    itemId);
            }

            string apiUrl =
                "https://api.gamebanana.com/Core/Item/Data" +
                "?itemtype=Mod" +
                "&itemid=" +
                parsedItemId +
                "&fields=Game().name" +
                "&format=json";

            using HttpClient client =
                new HttpClient();

            client.Timeout =
                TimeSpan.FromSeconds(30);

            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Infinity Modgen Manager");

            using HttpResponseMessage response =
                await client.GetAsync(
                    apiUrl);

            response.EnsureSuccessStatusCode();

            string json =
                await response.Content.ReadAsStringAsync();

            using JsonDocument document =
                JsonDocument.Parse(json);

            JsonElement root =
                document.RootElement;

            if (root.ValueKind ==
                JsonValueKind.Array &&
                root.GetArrayLength() > 0)
            {
                JsonElement firstValue =
                    root[0];

                if (firstValue.ValueKind ==
                    JsonValueKind.String)
                {
                    return firstValue.GetString();
                }
            }

            string keyedApiUrl =
                "https://api.gamebanana.com/Core/Item/Data" +
                "?itemtype=Mod" +
                "&itemid=" +
                parsedItemId +
                "&fields=Game().name" +
                "&return_keys=1" +
                "&format=json";

            using HttpResponseMessage keyedResponse =
                await client.GetAsync(
                    keyedApiUrl);

            keyedResponse.EnsureSuccessStatusCode();

            string keyedJson =
                await keyedResponse.Content.ReadAsStringAsync();

            using JsonDocument keyedDocument =
                JsonDocument.Parse(keyedJson);

            JsonElement keyedRoot =
                keyedDocument.RootElement;

            if (keyedRoot.ValueKind ==
                JsonValueKind.Object)
            {
                if (keyedRoot.TryGetProperty(
                        "Game().name",
                        out JsonElement gameNameElement))
                {
                    if (gameNameElement.ValueKind ==
                        JsonValueKind.String)
                    {
                        return gameNameElement.GetString();
                    }
                }
            }

            if (keyedRoot.ValueKind ==
                JsonValueKind.Array &&
                keyedRoot.GetArrayLength() > 0)
            {
                JsonElement firstObject =
                    keyedRoot[0];

                if (firstObject.ValueKind ==
                    JsonValueKind.Object &&
                    firstObject.TryGetProperty(
                        "Game().name",
                        out JsonElement arrayGameName))
                {
                    if (arrayGameName.ValueKind ==
                        JsonValueKind.String)
                    {
                        return arrayGameName.GetString();
                    }
                }
            }

            return null;
        }

        private Game? FindMatchingGame(
            string? gameName)
        {
            if (string.IsNullOrWhiteSpace(
                gameName))
            {
                return null;
            }

            string normalizedGameName =
                gameName.Trim();

            Game? exactMatch =
                games.FirstOrDefault(
                    game =>
                        string.Equals(
                            game.Name,
                            normalizedGameName,
                            StringComparison.OrdinalIgnoreCase));

            if (exactMatch != null)
                return exactMatch;

            Game? presetMatch =
                games.FirstOrDefault(
                    game =>
                        string.Equals(
                            game.Preset,
                            normalizedGameName,
                            StringComparison.OrdinalIgnoreCase));

            if (presetMatch != null)
                return presetMatch;

            if (normalizedGameName.Contains(
                    "Sonic Omens",
                    StringComparison.OrdinalIgnoreCase))
            {
                Game? sonicOmens =
                    games.FirstOrDefault(
                        game =>
                            string.Equals(
                                game.Preset,
                                "Sonic Omens",
                                StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(
                                game.Name,
                                "Sonic Omens",
                                StringComparison.OrdinalIgnoreCase));

                if (sonicOmens != null)
                    return sonicOmens;
            }

            return null;
        }

        private void AddGame_Click(
            object sender,
            RoutedEventArgs e)
        {
            OpenFileDialog dialog =
                new OpenFileDialog
                {
                    Title =
                        "Select Game Executable",

                    Filter =
                        "Game Executables (*.exe)|*.exe",

                    Multiselect =
                        false
                };

            bool? result =
                dialog.ShowDialog();

            if (result != true)
                return;

            string exePath =
                dialog.FileName;

            Game? existingGame =
                games.FirstOrDefault(
                    game =>
                        string.Equals(
                            game.ExePath,
                            exePath,
                            StringComparison.OrdinalIgnoreCase));

            if (existingGame != null)
            {
                try
                {
                    runtimeDeploymentService.EnsureShimInstalled(
                        existingGame);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(
                        "The game is already assigned, but the Infinity runtime could not be installed.\n\n" +
                        ex.Message,
                        "Infinity Modgen Manager",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);

                    return;
                }

                selectedGame =
                    existingGame;

                GameSelector.SelectedItem =
                    existingGame;

                ScanMods(
                    existingGame);

                UpdateSettingsPage();

                return;
            }

            Game? game =
                CreateGameFromExecutable(
                    exePath);

            if (game == null)
                return;

            try
            {
                runtimeDeploymentService.EnsureShimInstalled(
                    game);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "The game was added, but the Infinity runtime could not be installed.\n\n" +
                    ex.Message,
                    "Infinity Modgen Manager",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);

                return;
            }

            games.Add(game);

            selectedGame =
                game;

            GameSelector.SelectedItem =
                game;

            SaveGames();

            ScanMods(
                game);

            UpdateSettingsPage();
        }

        private Game? CreateGameFromExecutable(
            string exePath)
        {
            string gameDirectory =
                Path.GetDirectoryName(
                    exePath) ?? "";

            if (string.IsNullOrWhiteSpace(
                gameDirectory))
            {
                MessageBox.Show(
                    "Could not determine the game's directory.",
                    "Infinity Modgen Manager",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);

                return null;
            }

            string exeName =
                Path.GetFileName(
                    exePath);

            string contentDirectory =
                FindEngineContentDirectory(
                    gameDirectory);

            if (string.IsNullOrWhiteSpace(
                contentDirectory))
            {
                MessageBox.Show(
                    "Could not find the game's Unreal Engine Content folder.\n\n" +
                    "The selected executable does not appear to have a supported " +
                    "Infinity Engine / Unreal Engine content structure.",
                    "Infinity Modgen Manager",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);

                return null;
            }

            string paksDirectory =
                Path.Combine(
                    contentDirectory,
                    "Paks");

            Directory.CreateDirectory(
                paksDirectory);

            string modsDirectory =
                Path.Combine(
                    gameDirectory,
                    "Mods");

            Directory.CreateDirectory(
                modsDirectory);

            string preset =
                DetectPreset(
                    exePath,
                    gameDirectory,
                    contentDirectory);

            string iconPath =
                ExtractGameIcon(
                    exePath);

            string gameName =
                Path.GetFileNameWithoutExtension(
                    exeName);

            string engineVersion =
                "Unreal Engine 4.24";

            if (string.Equals(
                preset,
                "Sonic Omens",
                StringComparison.OrdinalIgnoreCase))
            {
                gameName =
                    "Sonic Omens";

                engineVersion =
                    "Unreal Engine 4.24";
            }

            return new Game
            {
                Name =
                    gameName,

                ExePath =
                    exePath,

                GameDirectory =
                    gameDirectory,

                EngineVersion =
                    engineVersion,

                ModsDirectory =
                    modsDirectory,

                EngineContentDirectory =
                    contentDirectory,

                PaksDirectory =
                    paksDirectory,

                SupportsPakMods =
                    true,

                Preset =
                    preset,

                IconPath =
                    iconPath
            };
        }

        private string DetectPreset(
            string exePath,
            string gameDirectory,
            string contentDirectory)
        {
            string exeName =
                Path.GetFileNameWithoutExtension(
                    exePath);

            if (exeName.Contains(
                    "Sonic",
                    StringComparison.OrdinalIgnoreCase) &&
                Directory.Exists(
                    Path.Combine(
                        gameDirectory,
                        "Sonic_Omens")))
            {
                return "Sonic Omens";
            }

            if (Directory.Exists(
                Path.Combine(
                    gameDirectory,
                    "Sonic_Omens")))
            {
                return "Sonic Omens";
            }

            return "Unreal Engine";
        }

        private string FindEngineContentDirectory(
            string gameDirectory)
        {
            string[] directories =
                Directory.GetDirectories(
                    gameDirectory);

            foreach (string directory in directories)
            {
                string contentDirectory =
                    Path.Combine(
                        directory,
                        "Content");

                if (Directory.Exists(
                    contentDirectory))
                {
                    return contentDirectory;
                }
            }

            string directContentDirectory =
                Path.Combine(
                    gameDirectory,
                    "Content");

            if (Directory.Exists(
                directContentDirectory))
            {
                return directContentDirectory;
            }

            return "";
        }

        private string ExtractGameIcon(
            string exePath)
        {
            try
            {
                Directory.CreateDirectory(
                    GamesConfigDirectory);

                string safeName =
                    Path.GetFileNameWithoutExtension(
                        exePath);

                foreach (char invalidCharacter in
                         Path.GetInvalidFileNameChars())
                {
                    safeName =
                        safeName.Replace(
                            invalidCharacter,
                            '_');
                }

                string iconPath =
                    Path.Combine(
                        GamesConfigDirectory,
                        safeName + ".png");

                using System.Drawing.Icon? icon =
                    System.Drawing.Icon.ExtractAssociatedIcon(
                        exePath);

                if (icon == null)
                    return "";

                using Bitmap bitmap =
                    icon.ToBitmap();

                bitmap.Save(
                    iconPath,
                    ImageFormat.Png);

                return iconPath;
            }
            catch
            {
                return "";
            }
        }

        private void LoadGames()
        {
            try
            {
                if (!File.Exists(GamesConfigPath))
                    return;

                string json =
                    File.ReadAllText(
                        GamesConfigPath);

                List<Game>? loadedGames =
                    JsonSerializer.Deserialize<List<Game>>(
                        json);

                if (loadedGames == null)
                    return;

                games.Clear();

                bool iconsChanged = false;

                foreach (Game game in loadedGames)
                {
                    if (string.IsNullOrWhiteSpace(game.IconPath) ||
                        !File.Exists(game.IconPath))
                    {
                        if (File.Exists(game.ExePath))
                        {
                            string iconPath =
                                ExtractGameIcon(
                                    game.ExePath);

                            if (!string.IsNullOrWhiteSpace(iconPath))
                            {
                                game.IconPath = iconPath;
                                iconsChanged = true;
                            }
                        }
                    }

                    try
                    {
                        runtimeDeploymentService.EnsureShimInstalled(
                            game);
                    }
                    catch
                    {
                        // Runtime deployment failure should not
                        // prevent the game from appearing.
                    }

                    games.Add(game);
                }

                if (iconsChanged)
                {
                    SaveGames();
                }

                if (games.Count > 0)
                {
                    GameSelector.SelectedIndex = 0;
                }
            }
            catch
            {
                games.Clear();
            }
        }

        private void SaveGames()
        {
            try
            {
                Directory.CreateDirectory(
                    GamesConfigDirectory);

                JsonSerializerOptions options =
                    new JsonSerializerOptions
                    {
                        WriteIndented = true
                    };

                string json =
                    JsonSerializer.Serialize(
                        games.ToArray(),
                        options);

                File.WriteAllText(
                    GamesConfigPath,
                    json);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "The game list could not be saved.\n\n" +
                    ex.Message,
                    "Infinity Modgen Manager",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }

        private void GameSelector_SelectionChanged(
            object sender,
            SelectionChangedEventArgs e)
        {
            if (GameSelector.SelectedItem is Game game)
            {
                selectedGame =
                    game;

                try
                {
                    runtimeDeploymentService.EnsureShimInstalled(
                        game);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(
                        "The game's runtime could not be installed.\n\n" +
                        ex.Message,
                        "Infinity Modgen Manager",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }

                ScanMods(
                    game);

                UpdateSettingsPage();
            }
        }

        private void RefreshMods()
        {
            if (selectedGame == null)
                return;

            ScanMods(
                selectedGame);

            UpdateSettingsPage();
        }

        private void RefreshMods_Click(
            object sender,
            RoutedEventArgs e)
        {
            RefreshMods();
        }

        private void ScanMods(
            Game game)
        {
            mods.Clear();

            string modsDirectory =
                game.ModsDirectory;

            if (string.IsNullOrWhiteSpace(
                modsDirectory))
            {
                modsDirectory =
                    Path.Combine(
                        game.GameDirectory,
                        "Mods");

                game.ModsDirectory =
                    modsDirectory;
            }

            Directory.CreateDirectory(
                modsDirectory);

            string[] modDirectories =
                Directory.GetDirectories(
                    modsDirectory);

            foreach (string modDirectory in modDirectories)
            {
                string iniPath =
                    Path.Combine(
                        modDirectory,
                        "mod.ini");

                if (!File.Exists(
                    iniPath))
                {
                    continue;
                }

                Mod mod =
                    ReadModIni(
                        iniPath,
                        modDirectory);

                mods.Add(
                    mod);
            }

            LoadModStates(
                game);

            LoadModOrder(
                game);
        }

        private Mod ReadModIni(
            string iniPath,
            string modDirectory)
        {
            Mod mod =
                new Mod
                {
                    ModDirectory =
                        modDirectory,

                    ModType =
                        "PAK"
                };

            string[] lines =
                File.ReadAllLines(
                    iniPath);

            foreach (string line in lines)
            {
                string trimmedLine =
                    line.Trim();

                if (string.IsNullOrWhiteSpace(
                    trimmedLine))
                {
                    continue;
                }

                if (trimmedLine.StartsWith(";"))
                    continue;

                if (trimmedLine.StartsWith("["))
                    continue;

                int equalsIndex =
                    trimmedLine.IndexOf('=');

                if (equalsIndex <= 0)
                    continue;

                string key =
                    trimmedLine.Substring(
                        0,
                        equalsIndex)
                    .Trim();

                string value =
                    trimmedLine.Substring(
                        equalsIndex + 1)
                    .Trim();

                switch (key.ToLower())
                {
                    case "name":
                        mod.Name =
                            value;
                        break;

                    case "author":
                        mod.Author =
                            value;
                        break;

                    case "version":
                        mod.Version =
                            value;
                        break;

                    case "description":
                        mod.Description =
                            value;
                        break;

                    case "pak":
                        mod.PakPath =
                            Path.Combine(
                                modDirectory,
                                value);

                        mod.PakFileName =
                            Path.GetFileName(
                                value);
                        break;

                    case "type":
                        mod.ModType =
                            value;
                        break;
                }
            }

            if (string.IsNullOrWhiteSpace(
                mod.PakFileName) &&
                !string.IsNullOrWhiteSpace(
                mod.PakPath))
            {
                mod.PakFileName =
                    Path.GetFileName(
                        mod.PakPath);
            }

            return mod;
        }

        private void LoadModStates(
            Game game)
        {
            string modsDirectory =
                game.ModsDirectory;

            string configPath =
                Path.Combine(
                    modsDirectory,
                    "loader.ini");

            if (!File.Exists(
                configPath))
            {
                return;
            }

            string[] lines =
                File.ReadAllLines(
                    configPath);

            foreach (string line in lines)
            {
                string trimmedLine =
                    line.Trim();

                if (string.IsNullOrWhiteSpace(
                    trimmedLine))
                {
                    continue;
                }

                if (trimmedLine.StartsWith(";"))
                    continue;

                int equalsIndex =
                    trimmedLine.IndexOf('=');

                if (equalsIndex <= 0)
                    continue;

                string modDirectoryName =
                    trimmedLine.Substring(
                        0,
                        equalsIndex)
                    .Trim();

                string enabledValue =
                    trimmedLine.Substring(
                        equalsIndex + 1)
                    .Trim();

                if (!bool.TryParse(
                    enabledValue,
                    out bool isEnabled))
                {
                    continue;
                }

                foreach (Mod mod in mods)
                {
                    string directoryName =
                        Path.GetFileName(
                            mod.ModDirectory);

                    if (directoryName ==
                        modDirectoryName)
                    {
                        mod.IsEnabled =
                            isEnabled;

                        break;
                    }
                }
            }
        }

        private void SaveModStates(
            Game game)
        {
            string modsDirectory =
                game.ModsDirectory;

            Directory.CreateDirectory(
                modsDirectory);

            string configPath =
                Path.Combine(
                    modsDirectory,
                    "loader.ini");

            using StreamWriter writer =
                new StreamWriter(
                    configPath,
                    false);

            writer.WriteLine(
                "; Infinity Modgen Manager configuration");

            foreach (Mod mod in mods)
            {
                string directoryName =
                    Path.GetFileName(
                        mod.ModDirectory);

                writer.WriteLine(
                    directoryName +
                    "=" +
                    mod.IsEnabled);
            }
        }

        private void LoadModOrder(
            Game game)
        {
            string modsDirectory =
                game.ModsDirectory;

            string configPath =
                Path.Combine(
                    modsDirectory,
                    "loader_order.ini");

            int defaultOrder = 0;

            foreach (Mod mod in mods)
            {
                mod.LoadOrder =
                    defaultOrder;

                defaultOrder++;
            }

            if (!File.Exists(
                configPath))
            {
                return;
            }

            string[] lines =
                File.ReadAllLines(
                    configPath);

            foreach (string line in lines)
            {
                string trimmedLine =
                    line.Trim();

                if (string.IsNullOrWhiteSpace(
                    trimmedLine))
                {
                    continue;
                }

                if (trimmedLine.StartsWith(";"))
                    continue;

                int equalsIndex =
                    trimmedLine.IndexOf('=');

                if (equalsIndex <= 0)
                    continue;

                string modDirectoryName =
                    trimmedLine.Substring(
                        0,
                        equalsIndex)
                    .Trim();

                string orderValue =
                    trimmedLine.Substring(
                        equalsIndex + 1)
                    .Trim();

                if (!int.TryParse(
                    orderValue,
                    out int loadOrder))
                {
                    continue;
                }

                foreach (Mod mod in mods)
                {
                    string directoryName =
                        Path.GetFileName(
                            mod.ModDirectory);

                    if (directoryName ==
                        modDirectoryName)
                    {
                        mod.LoadOrder =
                            loadOrder;

                        break;
                    }
                }
            }
        }

        private void SaveModOrder(
            Game game)
        {
            string modsDirectory =
                game.ModsDirectory;

            Directory.CreateDirectory(
                modsDirectory);

            string configPath =
                Path.Combine(
                    modsDirectory,
                    "loader_order.ini");

            using StreamWriter writer =
                new StreamWriter(
                    configPath,
                    false);

            writer.WriteLine(
                "; Infinity Modgen Manager mod load order");

            foreach (Mod mod in mods)
            {
                string directoryName =
                    Path.GetFileName(
                        mod.ModDirectory);

                writer.WriteLine(
                    directoryName +
                    "=" +
                    mod.LoadOrder);
            }
        }

        private void ModsButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            ModsPage.Visibility =
                Visibility.Visible;

            CodesPage.Visibility =
                Visibility.Collapsed;

            SettingsPage.Visibility =
                Visibility.Collapsed;

            SetActiveNavigation(
                ModsButton);
        }

        private void CodesButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            ModsPage.Visibility =
                Visibility.Collapsed;

            CodesPage.Visibility =
                Visibility.Visible;

            SettingsPage.Visibility =
                Visibility.Collapsed;

            SetActiveNavigation(
                CodesButton);
        }

        private void SettingsButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            ModsPage.Visibility =
                Visibility.Collapsed;

            CodesPage.Visibility =
                Visibility.Collapsed;

            SettingsPage.Visibility =
                Visibility.Visible;

            UpdateSettingsPage();

            SetActiveNavigation(
                SettingsButton);
        }

        private void SetActiveNavigation(
            Button activeButton)
        {
            ModsButton.Foreground =
                (System.Windows.Media.Brush)
                FindResource(
                    "SubTextBrush");

            CodesButton.Foreground =
                (System.Windows.Media.Brush)
                FindResource(
                    "SubTextBrush");

            SettingsButton.Foreground =
                (System.Windows.Media.Brush)
                FindResource(
                    "SubTextBrush");

            activeButton.Foreground =
                (System.Windows.Media.Brush)
                FindResource(
                    "BlueBrush");
        }

        private void UpdateSettingsPage()
        {
            if (selectedGame == null)
            {
                SettingsGameNameText.Text =
                    "No game selected";

                SettingsExePathText.Text =
                    "—";

                SettingsGameDirectoryText.Text =
                    "—";

                SettingsEngineVersionText.Text =
                    "—";

                SettingsContentDirectoryText.Text =
                    "—";

                SettingsPaksDirectoryText.Text =
                    "—";

                return;
            }

            SettingsGameNameText.Text =
                selectedGame.Name;

            SettingsExePathText.Text =
                string.IsNullOrWhiteSpace(
                    selectedGame.ExePath)
                    ? "—"
                    : selectedGame.ExePath;

            SettingsGameDirectoryText.Text =
                string.IsNullOrWhiteSpace(
                    selectedGame.GameDirectory)
                    ? "—"
                    : selectedGame.GameDirectory;

            SettingsEngineVersionText.Text =
                string.IsNullOrWhiteSpace(
                    selectedGame.EngineVersion)
                    ? "—"
                    : selectedGame.EngineVersion;

            SettingsContentDirectoryText.Text =
                string.IsNullOrWhiteSpace(
                    selectedGame.EngineContentDirectory)
                    ? "—"
                    : selectedGame.EngineContentDirectory;

            SettingsPaksDirectoryText.Text =
                string.IsNullOrWhiteSpace(
                    selectedGame.PaksDirectory)
                    ? "—"
                    : selectedGame.PaksDirectory;
        }

        private void OpenGameFolder_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (selectedGame == null)
            {
                MessageBox.Show(
                    "Please select a game first.",
                    "Infinity Modgen Manager",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);

                return;
            }

            OpenFolder(
                selectedGame.GameDirectory);
        }

        private void OpenModsFolder_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (selectedGame == null)
            {
                MessageBox.Show(
                    "Please select a game first.",
                    "Infinity Modgen Manager",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);

                return;
            }

            OpenFolder(
                selectedGame.ModsDirectory);
        }

        private void OpenFolder(
            string folderPath)
        {
            if (string.IsNullOrWhiteSpace(
                folderPath))
            {
                MessageBox.Show(
                    "The folder path is not configured.",
                    "Infinity Modgen Manager",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);

                return;
            }

            if (!Directory.Exists(
                folderPath))
            {
                MessageBox.Show(
                    "The folder could not be found.\n\n" +
                    folderPath,
                    "Infinity Modgen Manager",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);

                return;
            }

            try
            {
                Process.Start(
                    new ProcessStartInfo
                    {
                        FileName =
                            "explorer.exe",

                        Arguments =
                            "\"" +
                            folderPath +
                            "\"",

                        UseShellExecute =
                            true
                    });
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "The folder could not be opened.\n\n" +
                    ex.Message,
                    "Infinity Modgen Manager",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void ResetGameConfiguration_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (selectedGame == null)
            {
                MessageBox.Show(
                    "Please select a game first.",
                    "Infinity Modgen Manager",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);

                return;
            }

            MessageBoxResult result =
                MessageBox.Show(
                    "Remove \"" +
                    selectedGame.Name +
                    "\" from Infinity Modgen Manager?\n\n" +
                    "This will not uninstall or delete the game.",
                    "Reset Game Configuration",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes)
                return;

            Game gameToRemove =
                selectedGame;

            int removedIndex =
                games.IndexOf(
                    gameToRemove);

            games.Remove(
                gameToRemove);

            selectedGame =
                null;

            mods.Clear();

            SaveGames();

            if (games.Count > 0)
            {
                int newIndex =
                    Math.Min(
                        removedIndex,
                        games.Count - 1);

                GameSelector.SelectedIndex =
                    newIndex;

                selectedGame =
                    games[newIndex];

                ScanMods(
                    selectedGame);
            }
            else
            {
                GameSelector.SelectedItem =
                    null;
            }

            UpdateSettingsPage();

            MessageBox.Show(
                "The game configuration has been removed.",
                "Infinity Modgen Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        private void InstallMod_Click(
            object sender,
            RoutedEventArgs e)
        {
            MessageBox.Show(
                "GameBanana 1-Click installation is handled automatically when a GameBanana 1-Click link is opened.",
                "Install Mod",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        private void InspectPak_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (!(sender is Button button))
                return;

            if (!(button.DataContext is Mod mod))
                return;

            if (string.IsNullOrWhiteSpace(
                mod.ModDirectory))
            {
                MessageBox.Show(
                    "This mod does not have a valid directory.",
                    "PAK Inspector",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);

                return;
            }

            try
            {
                PakManifest manifest =
                    pakInspector.InspectMod(
                        mod.ModDirectory);

                StringBuilder result =
                    new StringBuilder();

                result.AppendLine(
                    "Mod: " +
                    mod.Name);

                result.AppendLine();

                result.AppendLine(
                    "PAK files: " +
                    manifest.PakFiles.Count);

                result.AppendLine(
                    "Files inside archives: " +
                    manifest.TotalFiles);

                result.AppendLine(
                    "Total archive size: " +
                    FormatFileSize(
                        manifest.TotalSize));

                result.AppendLine();

                result.AppendLine(
                    "Archives:");

                foreach (PakFileInfo pak in
                         manifest.PakFiles)
                {
                    result.AppendLine(
                        "  " +
                        pak.FileName +
                        " (" +
                        FormatFileSize(
                            pak.FileSize) +
                        ")");
                }

                result.AppendLine();

                result.AppendLine(
                    "First files:");

                int displayCount =
                    Math.Min(
                        manifest.Files.Count,
                        20);

                for (int i = 0;
                     i < displayCount;
                     i++)
                {
                    result.AppendLine(
                        "  " +
                        manifest.Files[i].Path);
                }

                if (manifest.Files.Count > 20)
                {
                    result.AppendLine();
                    result.AppendLine(
                        "...and " +
                        (manifest.Files.Count - 20) +
                        " more files.");
                }

                MessageBox.Show(
                    result.ToString(),
                    "PAK Inspector",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "CUE4Parse could not inspect this mod.\n\n" +
                    ex.Message,
                    "PAK Inspector",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private string FormatFileSize(
            long bytes)
        {
            if (bytes < 1024)
                return bytes + " B";

            if (bytes < 1024 * 1024)
                return
                    (bytes / 1024.0)
                    .ToString("0.00") +
                    " KB";

            if (bytes < 1024L * 1024L * 1024L)
                return
                    (bytes / (1024.0 * 1024.0))
                    .ToString("0.00") +
                    " MB";

            return
                (bytes / (1024.0 * 1024.0 * 1024.0))
                .ToString("0.00") +
                " GB";
        }

private void LaunchGame_Click(
    object sender,
    RoutedEventArgs e)
        {
            if (selectedGame == null)
            {
                MessageBox.Show(
                    "Please select a game first.",
                    "Infinity Modgen Manager",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);

                return;
            }

            if (string.IsNullOrWhiteSpace(
                selectedGame.ExePath) ||
                !File.Exists(
                selectedGame.ExePath))
            {
                MessageBox.Show(
                    "The game's executable could not be found.\n\n" +
                    selectedGame.ExePath,
                    "Infinity Modgen Manager",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);

                return;
            }

            try
            {
                runtimeDeploymentService.EnsureShimInstalled(
                    selectedGame);

                SaveModStates(
                    selectedGame);

                SaveModOrder(
                    selectedGame);

                pakManager.ApplyMods(
                    selectedGame,
                    mods);

                string configDirectory =
                    Path.Combine(
                        selectedGame.ModsDirectory,
                        "Config");

                Directory.CreateDirectory(
                    selectedGame.ModsDirectory);

                Directory.CreateDirectory(
                    configDirectory);

                SaveGames();

                string launchArguments =
                    "--mod-dir \"" +
                    selectedGame.ModsDirectory +
                    "\" " +
                    "--pak-dir \"" +
                    selectedGame.ModsDirectory +
                    "\" " +
                    "--cfg-dir \"" +
                    configDirectory +
                    "\"";

                SteamShortcut? shortcut =
                    SteamService.FindShortcutForExecutable(
                        selectedGame.ExePath);

                if (shortcut != null &&
                    shortcut.AppId != 0)
                {
                    selectedGame.SteamAppId =
                        shortcut.AppId.ToString();

                    SaveGames();

                    bool launchedThroughSteam =
                        SteamService.TryLaunchByAppId(
                            shortcut.AppId);

                    if (!launchedThroughSteam)
                    {
                        ProcessStartInfo fallbackStartInfo =
                            new ProcessStartInfo
                            {
                                FileName =
                                    selectedGame.ExePath,

                                WorkingDirectory =
                                    selectedGame.GameDirectory,

                                Arguments =
                                    launchArguments,

                                UseShellExecute =
                                    true
                            };

                        Process.Start(
                            fallbackStartInfo);
                    }

                    return;
                }

                ProcessStartInfo startInfo =
                    new ProcessStartInfo
                    {
                        FileName =
                            selectedGame.ExePath,

                        WorkingDirectory =
                            selectedGame.GameDirectory,

                        Arguments =
                            launchArguments,

                        UseShellExecute =
                            true
                    };

                Process.Start(
                    startInfo);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "The game could not be launched.\n\n" +
                    ex.Message,
                    "Infinity Modgen Manager",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }


        private void TestSteamOmens_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (selectedGame == null)
            {
                MessageBox.Show(
                    "Please select a game first.",
                    "Steam Test",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);

                return;
            }

            try
            {
                SteamShortcut? shortcut =
                    SteamService.FindShortcutForExecutable(
                        selectedGame.ExePath);

                if (shortcut == null)
                {
                    MessageBox.Show(
                        selectedGame.Name +
                        " could not be found in your Steam non-Steam shortcuts.\n\n" +
                        "SteamService searched the Steam userdata folders and shortcuts.vdf.",
                        "Steam Test",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);

                    return;
                }

                MessageBox.Show(
                    selectedGame.Name +
                    " Steam shortcut found!\n\n" +
                    "Name: " +
                    shortcut.AppName +
                    "\n\n" +
                    "App ID: " +
                    shortcut.AppId +
                    "\n\n" +
                    "Executable:\n" +
                    shortcut.Exe +
                    "\n\n" +
                    "Start Directory:\n" +
                    shortcut.StartDir +
                    "\n\n" +
                    "Steam URI:\n" +
                    "steam://rungameid/" +
                    shortcut.AppId,
                    "Steam Test",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "The Steam shortcut test failed.\n\n" +
                    ex.Message,
                    "Steam Test",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void FixRuntimeDlls_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (selectedGame == null)
            {
                MessageBox.Show(
                    "Please select a game first.",
                    "Fix Runtime DLLs",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);

                return;
            }

            try
            {
                runtimeDeploymentService.ForceRepairRuntime(
                    selectedGame);

                MessageBox.Show(
                    "The runtime DLLs have been repaired successfully.\n\n" +
                    "dwmapi.dll and ue4ss.dll have been updated for:\n" +
                    selectedGame.Name,
                    "Fix Runtime DLLs",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "The runtime DLLs could not be repaired.\n\n" +
                    ex.Message,
                    "Fix Runtime DLLs",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void Save_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (selectedGame == null)
            {
                MessageBox.Show(
                    "Please select a game first.",
                    "Infinity Modgen Manager",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);

                return;
            }

            try
            {
                SaveModStates(
                    selectedGame);

                SaveModOrder(
                    selectedGame);

                pakManager.ApplyMods(
                    selectedGame,
                    mods);

                SaveGames();

                MessageBox.Show(
                    "Mod configuration saved and applied.",
                    "Infinity Modgen Manager",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "Failed to apply mods.\n\n" +
                    ex.Message,
                    "Infinity Modgen Manager",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }
    }
}

