using InfinityModgenManager.Models;
using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;

namespace InfinityModgenManager.Services
{
    public class RuntimeDeploymentService
    {
        private const string ShimFileName = "dwmapi.dll";
        private const string ExpectedProductName = "unreal_shimloader";

        private const string Ue4ssFileName = "ue4ss.dll";

        public void EnsureShimInstalled(Game game)
        {
            DeployRuntime(
                game,
                false);
        }

        public void ForceRepairRuntime(Game game)
        {
            DeployRuntime(
                game,
                true);
        }

        private void DeployRuntime(
            Game game,
            bool forceRepair)
        {
            if (game == null)
            {
                throw new ArgumentNullException(nameof(game));
            }

            if (string.IsNullOrWhiteSpace(game.ExePath))
            {
                throw new IOException(
                    "The game's executable path has not been configured.");
            }

            if (!File.Exists(game.ExePath))
            {
                throw new FileNotFoundException(
                    "The game's executable could not be found.",
                    game.ExePath);
            }

            /*
             * EngineContentDirectory points to:
             *
             *     Sonic_Omens\Content
             *
             * Therefore its parent directory is:
             *
             *     Sonic_Omens
             *
             * The runtime DLLs must be placed specifically in:
             *
             *     Sonic_Omens\Binaries\Win64
             */
            if (string.IsNullOrWhiteSpace(game.EngineContentDirectory))
            {
                throw new IOException(
                    "The game's Unreal Engine Content directory has not been configured.");
            }

            string? gameRootDirectory =
                Path.GetDirectoryName(
                    game.EngineContentDirectory);

            if (string.IsNullOrWhiteSpace(gameRootDirectory))
            {
                throw new IOException(
                    "Could not determine the game's Unreal Engine root directory.");
            }

            /*
             * This is the ONLY directory where the runtime DLLs
             * should be installed.
             */
            string gameBinaryDirectory =
                Path.Combine(
                    gameRootDirectory,
                    "Binaries",
                    "Win64");

            if (!Directory.Exists(gameBinaryDirectory))
            {
                throw new DirectoryNotFoundException(
                    "The game's Unreal Engine Win64 binary directory could not be found.\n\n" +
                    gameBinaryDirectory);
            }

            /*
             * ---------------------------------------------------------
             * DEPLOY / UPDATE dwmapi.dll
             * ---------------------------------------------------------
             *
             * The Rust runtime is built here:
             *
             *     Runtime\unreal-shimloader\target\release\
             *
             * Cargo produces:
             *
             *     dwmapi.dll
             *
             * The Manager installs it into the game as:
             *
             *     dwmapi.dll
             */

            string sourceShim =
                Path.Combine(
                    AppContext.BaseDirectory,
                    "Runtime",
                    "unreal-shimloader",
                    "target",
                    "release",
                    "dwmapi.dll");

            if (!File.Exists(sourceShim))
            {
                throw new FileNotFoundException(
                    "The Infinity Modgen runtime shim could not be found in the Manager installation.\n\n" +
                    "Build the unreal-shimloader release configuration first.",
                    sourceShim);
            }

            string destinationShim =
                Path.Combine(
                    gameBinaryDirectory,
                    ShimFileName);

            if (File.Exists(destinationShim))
            {
                FileVersionInfo existingInfo =
                    FileVersionInfo.GetVersionInfo(
                        destinationShim);

                /*
                 * Never overwrite a completely different dwmapi.dll.
                 */
                if (!string.Equals(
                    existingInfo.ProductName,
                    ExpectedProductName,
                    StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException(
                        "A different dwmapi.dll already exists beside the Unreal game executable.\n\n" +
                        "The Manager will not overwrite it:\n" +
                        destinationShim);
                }

                /*
                 * During a normal launch, only replace our shim if
                 * the installed file differs from the current Manager
                 * runtime.
                 *
                 * The SHA-256 comparison allows us to detect an older
                 * or corrupted copy even when its file version is the same.
                 */
                bool needsUpdate =
                    !FilesAreIdentical(
                        sourceShim,
                        destinationShim);

                if (forceRepair || needsUpdate)
                {
                    File.Copy(
                        sourceShim,
                        destinationShim,
                        true);
                }
            }
            else
            {
                File.Copy(
                    sourceShim,
                    destinationShim,
                    false);
            }

            /*
             * ---------------------------------------------------------
             * DEPLOY / UPDATE ue4ss.dll
             * ---------------------------------------------------------
             */

            string sourceUe4ss =
                Path.Combine(
                    AppContext.BaseDirectory,
                    "Runtime",
                    "unreal-shimloader",
                    "ts",
                    "UE4SS",
                    "UE4SS.dll");

            if (!File.Exists(sourceUe4ss))
            {
                throw new FileNotFoundException(
                    "The UE4SS runtime DLL could not be found in the Manager installation.",
                    sourceUe4ss);
            }

            string destinationUe4ss =
                Path.Combine(
                    gameBinaryDirectory,
                    Ue4ssFileName);

            /*
             * If UE4SS is missing, install it.
             *
             * If it already exists, only update it when the bundled
             * version differs, unless the user explicitly requests
             * a runtime repair.
             */
            if (!File.Exists(destinationUe4ss) ||
                forceRepair ||
                !FilesAreIdentical(
                    sourceUe4ss,
                    destinationUe4ss))
            {
                File.Copy(
                    sourceUe4ss,
                    destinationUe4ss,
                    true);
            }
        }

        private static bool FilesAreIdentical(
            string firstFile,
            string secondFile)
        {
            if (!File.Exists(firstFile) ||
                !File.Exists(secondFile))
            {
                return false;
            }

            FileInfo firstInfo =
                new FileInfo(firstFile);

            FileInfo secondInfo =
                new FileInfo(secondFile);

            if (firstInfo.Length != secondInfo.Length)
            {
                return false;
            }

            using SHA256 sha256 =
                SHA256.Create();

            using FileStream firstStream =
                File.OpenRead(firstFile);

            using FileStream secondStream =
                File.OpenRead(secondFile);

            byte[] firstHash =
                sha256.ComputeHash(
                    firstStream);

            byte[] secondHash =
                sha256.ComputeHash(
                    secondStream);

            return CryptographicOperations.FixedTimeEquals(
                firstHash,
                secondHash);
        }
    }
}