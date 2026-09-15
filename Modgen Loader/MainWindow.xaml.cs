using Microsoft.Win32;
using Modgen_Loader.Models;
using Modgen_Loader.Services;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace Modgen_Loader
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

        private Game? selectedGame;

        private string GamesConfigDirectory
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(
                        Environment.SpecialFolder.ApplicationData),
                    "Infinity Modgen Loader");
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

            GameSelector.ItemsSource = games;
            ModsList.ItemsSource = mods;

            LoadGames();
        }

        private void AddGame_Click(
            object sender,
            RoutedEventArgs e)
        {
            OpenFileDialog dialog = new OpenFileDialog
            {
                Title = "Select Game Executable",
                Filter = "Game Executables (*.exe)|*.exe",
                Multiselect = false
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
                    "Infinity Modgen Loader",
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
                    "Infinity Modgen Loader",
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
            games.Clear();

            if (!File.Exists(
                GamesConfigPath))
            {
                UpdateSettingsPage();
                return;
            }

            try
            {
                string json =
                    File.ReadAllText(
                        GamesConfigPath);

                Game[]? savedGames =
                    JsonSerializer.Deserialize<Game[]>(
                        json);

                if (savedGames == null)
                {
                    UpdateSettingsPage();
                    return;
                }

                foreach (Game game in savedGames)
                {
                    if (string.IsNullOrWhiteSpace(
                        game.ExePath))
                    {
                        continue;
                    }

                    if (!File.Exists(
                        game.ExePath))
                    {
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(
                        game.Preset))
                    {
                        game.Preset =
                            "Unreal Engine";
                    }

                    games.Add(
                        game);
                }

                if (games.Count > 0)
                {
                    selectedGame =
                        games[0];

                    GameSelector.SelectedItem =
                        selectedGame;

                    ScanMods(
                        selectedGame);
                }

                UpdateSettingsPage();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "The saved game list could not be loaded.\n\n" +
                    ex.Message,
                    "Infinity Modgen Loader",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
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
                    "Infinity Modgen Loader",
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

                ScanMods(
                    game);

                UpdateSettingsPage();
            }
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
            Mod mod = new Mod
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

            using (StreamWriter writer =
                   new StreamWriter(
                       configPath,
                       false))
            {
                writer.WriteLine(
                    "; Infinity Modgen Loader configuration");

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

            using (StreamWriter writer =
                   new StreamWriter(
                       configPath,
                       false))
            {
                writer.WriteLine(
                    "; Infinity Modgen Loader mod load order");

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
                    "Infinity Modgen Loader",
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
                    "Infinity Modgen Loader",
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
                    "Infinity Modgen Loader",
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
                    "Infinity Modgen Loader",
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
                    "Infinity Modgen Loader",
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
                    "Infinity Modgen Loader",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);

                return;
            }

            MessageBoxResult result =
                MessageBox.Show(
                    "Remove \"" +
                    selectedGame.Name +
                    "\" from Infinity Modgen Loader?\n\n" +
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
                "Infinity Modgen Loader",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        private void InstallMod_Click(
            object sender,
            RoutedEventArgs e)
        {
            MessageBox.Show(
                "Mod installation will be added here.",
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
                    "Infinity Modgen Loader",
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
                    "Infinity Modgen Loader",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);

                return;
            }

            try
            {
                ProcessStartInfo startInfo =
                    new ProcessStartInfo
                    {
                        FileName =
                            selectedGame.ExePath,

                        WorkingDirectory =
                            selectedGame.GameDirectory,

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
                    "Infinity Modgen Loader",
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
                    "Infinity Modgen Loader",
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
                    "Infinity Modgen Loader",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "Failed to apply mods.\n\n" +
                    ex.Message,
                    "Infinity Modgen Loader",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }
    }
}