namespace Modgen_Loader.Models
{
    public class Game
    {
        public string Name { get; set; } = "";
        public string ExePath { get; set; } = "";
        public string GameDirectory { get; set; } = "";
        public string EngineVersion { get; set; } = "";

        public string ModsDirectory { get; set; } = "";
        public string EngineContentDirectory { get; set; } = "";
        public string PaksDirectory { get; set; } = "";

        public bool SupportsPakMods { get; set; } = true;

        public string Preset { get; set; } = "";

        public string IconPath { get; set; } = "";
    }
}