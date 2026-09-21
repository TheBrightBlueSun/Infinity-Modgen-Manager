using InfinityModgenManager.Models;
using SharpCompress.Archives;
using SharpCompress.Common;
using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;

namespace InfinityModgenManager
{
    public partial class GameBananaInstallWindow : Window
    {
        private readonly Game selectedGame;
        private readonly string downloadUrl;
        private readonly string? gameBananaModId;

        private readonly string modName;
        private readonly string modAuthor;
        private readonly string modDescription;
        private readonly string modVersion;
        private readonly string? thumbnailUrl;

        public GameBananaInstallWindow(
            Game game,
            string downloadUrl,
            string? gameBananaModId,
            string modName,
            string modAuthor,
            string modDescription,
            string modVersion,
            string? thumbnailUrl)
        {
            InitializeComponent();

            selectedGame = game;
            this.downloadUrl = downloadUrl;
            this.gameBananaModId = gameBananaModId;

            this.modName =
                string.IsNullOrWhiteSpace(modName)
                    ? "Unknown Mod"
                    : modName;

            this.modAuthor =
                string.IsNullOrWhiteSpace(modAuthor)
                    ? "Unknown"
                    : modAuthor;

            this.modDescription =
                string.IsNullOrWhiteSpace(modDescription)
                    ? "No description available."
                    : modDescription;

            this.modVersion =
                string.IsNullOrWhiteSpace(modVersion)
                    ? "Unknown"
                    : modVersion;

            this.thumbnailUrl =
                thumbnailUrl;

            PopulateMetadata();
        }

        private void PopulateMetadata()
        {
            ModNameText.Text =
                modName;

            ModAuthorText.Text =
                "by " +
                modAuthor;

            ModDescriptionText.Text =
                modDescription;

            ModVersionText.Text =
                "Version: " +
                modVersion;

            ModIdText.Text =
                "GameBanana ID: " +
                (string.IsNullOrWhiteSpace(
                    gameBananaModId)
                    ? "Unknown"
                    : gameBananaModId);

            if (!string.IsNullOrWhiteSpace(
                thumbnailUrl))
            {
                try
                {
                    BitmapImage image =
                        new BitmapImage();

                    image.BeginInit();

                    image.UriSource =
                        new Uri(
                            thumbnailUrl,
                            UriKind.Absolute);

                    image.CacheOption =
                        BitmapCacheOption.OnLoad;

                    image.EndInit();

                    ModThumbnail.Source =
                        image;
                }
                catch
                {
                    ModThumbnail.Source =
                        null;
                }
            }
        }

        private void CancelButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private async void InstallButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            ConfirmationPanel.Visibility =
                Visibility.Collapsed;

            ProgressPanel.Visibility =
                Visibility.Visible;

            InstallButton.IsEnabled =
                false;

            CancelButton.IsEnabled =
                false;

            ProgressStatusText.Text =
                "Preparing...";

            ProgressDetailsText.Text =
                "Preparing the mod for installation.";

            DownloadProgressBar.Value =
                0;

            try
            {
                await InstallModAsync();

                ProgressStatusText.Text =
                    "Installation complete";

                ProgressDetailsText.Text =
                    "The mod has been installed successfully.";

                DownloadProgressBar.Value =
                    100;

                await Task.Delay(
                    500);

                DialogResult =
                    true;

                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    this,
                    $"The mod could not be installed.\n\n{ex.Message}",
                    "Installation Failed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);

                ConfirmationPanel.Visibility =
                    Visibility.Visible;

                ProgressPanel.Visibility =
                    Visibility.Collapsed;

                InstallButton.IsEnabled =
                    true;

                CancelButton.IsEnabled =
                    true;
            }
        }

        private async Task InstallModAsync()
        {
            if (string.IsNullOrWhiteSpace(
                selectedGame.ModsDirectory))
            {
                throw new InvalidOperationException(
                    "The selected game does not have a Mods directory configured.");
            }

            if (!Directory.Exists(
                selectedGame.ModsDirectory))
            {
                Directory.CreateDirectory(
                    selectedGame.ModsDirectory);
            }

            if (!Uri.TryCreate(
                    downloadUrl,
                    UriKind.Absolute,
                    out Uri? downloadUri) ||
                (downloadUri.Scheme !=
                 Uri.UriSchemeHttp &&
                 downloadUri.Scheme !=
                 Uri.UriSchemeHttps))
            {
                throw new InvalidOperationException(
                    "The GameBanana download URL is invalid.");
            }

            string tempRoot =
                Path.Combine(
                    Path.GetTempPath(),
                    "InfinityModgen",
                    "GameBanana",
                    Guid.NewGuid().ToString("N"));

            string archiveFileName =
                Path.GetFileName(
                    downloadUri.AbsolutePath);

            if (string.IsNullOrWhiteSpace(
                archiveFileName))
            {
                archiveFileName =
                    "GameBananaDownload";
            }

            string extractedDirectory =
                Path.Combine(
                    tempRoot,
                    "Extracted");

            string archivePath =
                Path.Combine(
                    tempRoot,
                    archiveFileName);

            try
            {
                Directory.CreateDirectory(
                    tempRoot);

                Directory.CreateDirectory(
                    extractedDirectory);

                /*
                 * DOWNLOAD
                 *
                 * The HTTP response and every associated stream
                 * must be completely disposed before SharpCompress
                 * opens the archive.
                 */
                ProgressStatusText.Text =
                    "Downloading...";

                ProgressDetailsText.Text =
                    downloadUri.ToString();

                DownloadProgressBar.Value =
                    10;

                using (HttpClient client =
                       new HttpClient
                       {
                           Timeout =
                               TimeSpan.FromMinutes(10)
                       })
                {
                    client.DefaultRequestHeaders.UserAgent.ParseAdd(
                        "Infinity Modgen Manager");

                    using (HttpResponseMessage response =
                           await client.GetAsync(
                               downloadUri,
                               HttpCompletionOption.ResponseHeadersRead))
                    {
                        response.EnsureSuccessStatusCode();

                        string? serverFileName =
                            response.Content.Headers
                                .ContentDisposition?
                                .FileNameStar;

                        if (string.IsNullOrWhiteSpace(
                            serverFileName))
                        {
                            serverFileName =
                                response.Content.Headers
                                    .ContentDisposition?
                                    .FileName;
                        }

                        if (!string.IsNullOrWhiteSpace(
                            serverFileName))
                        {
                            archiveFileName =
                                serverFileName.Trim('"');

                            archivePath =
                                Path.Combine(
                                    tempRoot,
                                    archiveFileName);
                        }

                        using (Stream input =
                               await response.Content.ReadAsStreamAsync())
                        using (FileStream output =
                               new FileStream(
                                   archivePath,
                                   FileMode.Create,
                                   FileAccess.Write,
                                   FileShare.None))
                        {
                            await input.CopyToAsync(
                                output);

                            await output.FlushAsync();
                        }
                    }
                }

                /*
                 * At this point:
                 *
                 * - HttpClient is disposed.
                 * - HttpResponseMessage is disposed.
                 * - HTTP content stream is disposed.
                 * - FileStream is disposed.
                 *
                 * SharpCompress can now safely open the archive.
                 */

                ProgressStatusText.Text =
                    "Extracting...";

                ProgressDetailsText.Text =
                    $"Extracting {archiveFileName}.";

                DownloadProgressBar.Value =
                    30;

                ExtractGameBananaArchive(
                    archivePath,
                    extractedDirectory);

                ProgressStatusText.Text =
                    "Finding mod files...";

                ProgressDetailsText.Text =
                    "Looking for the mod's files.";

                DownloadProgressBar.Value =
                    50;

                string[] pakFiles =
                    Directory.GetFiles(
                        extractedDirectory,
                        "*.pak",
                        SearchOption.AllDirectories);

                if (pakFiles.Length == 0)
                {
                    throw new InvalidOperationException(
                        "No .pak file was found in the downloaded GameBanana archive.");
                }

                string modName =
                    DetermineInstalledModName(
                        extractedDirectory,
                        downloadUri,
                        gameBananaModId);

                string safeModName =
                    SanitizeModDirectoryName(
                        modName);

                string destinationDirectory =
                    Path.Combine(
                        selectedGame.ModsDirectory,
                        safeModName);

                int duplicateNumber =
                    2;

                while (Directory.Exists(
                    destinationDirectory))
                {
                    destinationDirectory =
                        Path.Combine(
                            selectedGame.ModsDirectory,
                            $"{safeModName}_{duplicateNumber}");

                    duplicateNumber++;
                }

                string installationDirectory =
                    FindModInstallationDirectory(
                        extractedDirectory,
                        pakFiles);

                ProgressStatusText.Text =
                    "Installing...";

                ProgressDetailsText.Text =
                    $"Installing {modName}.";

                DownloadProgressBar.Value =
                    70;

                Directory.CreateDirectory(
                    destinationDirectory);

                CopyDirectoryContents(
                    installationDirectory,
                    destinationDirectory);

                ProgressStatusText.Text =
                    "Finishing...";

                ProgressDetailsText.Text =
                    "Finalising the installation.";

                DownloadProgressBar.Value =
                    90;
            }
            finally
            {
                try
                {
                    if (Directory.Exists(
                        tempRoot))
                    {
                        Directory.Delete(
                            tempRoot,
                            true);
                    }
                }
                catch
                {
                    // Ignore temporary cleanup failures.
                }
            }
        }

        private static string FindModInstallationDirectory(
            string extractedDirectory,
            string[] pakFiles)
        {
            string[] iniFiles =
                Directory.GetFiles(
                    extractedDirectory,
                    "mod.ini",
                    SearchOption.AllDirectories);

            if (iniFiles.Length > 0)
            {
                string? modIniDirectory =
                    Path.GetDirectoryName(
                        iniFiles[0]);

                if (!string.IsNullOrWhiteSpace(
                    modIniDirectory))
                {
                    return modIniDirectory;
                }
            }

            if (pakFiles.Length > 0)
            {
                string? pakDirectory =
                    Path.GetDirectoryName(
                        pakFiles[0]);

                if (!string.IsNullOrWhiteSpace(
                    pakDirectory))
                {
                    return pakDirectory;
                }
            }

            return extractedDirectory;
        }

        private static void ExtractGameBananaArchive(
            string archivePath,
            string destinationDirectory)
        {
            using var archive =
                ArchiveFactory.Open(
                    archivePath);

            foreach (var entry in archive.Entries)
            {
                if (entry.IsDirectory)
                    continue;

                string key =
                    entry.Key.Replace(
                        '\\',
                        '/');

                if (Path.IsPathRooted(
                    key))
                {
                    throw new InvalidOperationException(
                        "The archive contains an invalid absolute path.");
                }

                string fullPath =
                    Path.GetFullPath(
                        Path.Combine(
                            destinationDirectory,
                            key));

                string fullDestination =
                    Path.GetFullPath(
                        destinationDirectory);

                if (!fullPath.StartsWith(
                        fullDestination +
                        Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        "The archive contains an invalid path.");
                }

                string? directory =
                    Path.GetDirectoryName(
                        fullPath);

                if (!string.IsNullOrEmpty(
                    directory))
                {
                    Directory.CreateDirectory(
                        directory);
                }

                entry.WriteToFile(
                    fullPath,
                    new ExtractionOptions
                    {
                        ExtractFullPath = true,
                        Overwrite = true
                    });
            }
        }

        private static string DetermineInstalledModName(
            string extractedDirectory,
            Uri downloadUri,
            string? gameBananaModId)
        {
            string[] iniFiles =
                Directory.GetFiles(
                    extractedDirectory,
                    "mod.ini",
                    SearchOption.AllDirectories);

            foreach (string iniFile in iniFiles)
            {
                try
                {
                    foreach (string line in
                             File.ReadAllLines(
                                 iniFile))
                    {
                        string trimmed =
                            line.Trim();

                        if (trimmed.StartsWith(
                                "name=",
                                StringComparison.OrdinalIgnoreCase))
                        {
                            string name =
                                trimmed.Substring(
                                    5)
                                .Trim();

                            if (!string.IsNullOrWhiteSpace(
                                name))
                            {
                                return name;
                            }
                        }
                    }
                }
                catch
                {
                    // Ignore invalid mod.ini files.
                }
            }

            string fileName =
                Path.GetFileNameWithoutExtension(
                    downloadUri.AbsolutePath);

            if (!string.IsNullOrWhiteSpace(
                fileName))
            {
                return fileName;
            }

            if (!string.IsNullOrWhiteSpace(
                gameBananaModId))
            {
                return
                    $"GameBanana Mod {gameBananaModId}";
            }

            return "GameBanana Mod";
        }

        private static string SanitizeModDirectoryName(
            string name)
        {
            foreach (char invalidCharacter in
                     Path.GetInvalidFileNameChars())
            {
                name =
                    name.Replace(
                        invalidCharacter,
                        '_');
            }

            name =
                name.Trim();

            if (string.IsNullOrWhiteSpace(
                name))
            {
                name =
                    "GameBanana Mod";
            }

            return name;
        }

        private static void CopyDirectoryContents(
            string sourceDirectory,
            string destinationDirectory)
        {
            Directory.CreateDirectory(
                destinationDirectory);

            foreach (string file in
                     Directory.GetFiles(
                         sourceDirectory,
                         "*",
                         SearchOption.AllDirectories))
            {
                string relativePath =
                    Path.GetRelativePath(
                        sourceDirectory,
                        file);

                string destinationFile =
                    Path.Combine(
                        destinationDirectory,
                        relativePath);

                string? destinationFolder =
                    Path.GetDirectoryName(
                        destinationFile);

                if (!string.IsNullOrEmpty(
                    destinationFolder))
                {
                    Directory.CreateDirectory(
                        destinationFolder);
                }

                File.Copy(
                    file,
                    destinationFile,
                    true);
            }
        }
    }
}