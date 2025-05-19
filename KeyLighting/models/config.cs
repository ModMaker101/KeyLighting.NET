using System.Drawing;
using Newtonsoft.Json;

namespace KeyboardLighting
{
    public class LightingConfig
    {
        [JsonProperty("version")]
        public string Version { get; set; } = "1.1.0";

        [JsonProperty("numKeys")]
        public int NumKeys { get; set; } = 104;

        [JsonProperty("downscaleHeight")]
        public int DownscaleHeight { get; set; } = 24;

        [JsonProperty("updateDelayMs")]
        public int UpdateDelayMs { get; set; } = 16;

        [JsonProperty("brightnessMultiplier")]
        public double BrightnessMultiplier { get; set; } = 1.5;

        [JsonProperty("vibranceFactor")]
        public double VibranceFactor { get; set; } = 1.6;

        [JsonProperty("contrastPower")]
        public double ContrastPower { get; set; } = 0.85;

        [JsonProperty("fadeFactor")]
        public double FadeFactor { get; set; } = 0.85;

        [JsonProperty("darkenThreshold")]
        public int DarkenThreshold { get; set; } = 40;

        [JsonProperty("darkenFactor")]
        public double DarkenFactor { get; set; } = 0.5;

        [JsonProperty("wasdEnabled")]
        public bool WASDEnabled { get; set; } = false;

        [JsonProperty("wasdKeys")]
        public int[] WASDKeys { get; set; } = new int[4] { 29, 36, 37, 43 };

        [JsonProperty("wasdColor")]
        public ColorConfig? WASDColor { get; set; } = new ColorConfig
        {
            R = 255,
            G = 0,
            B = 0
        };

        [JsonProperty("debugStringUpdates")]
        public bool DebugStringUpdates { get; set; } = false;

        [JsonProperty("saveDebugImages")]
        public bool SaveDebugImages { get; set; } = false;

        [JsonProperty("monitorSettings")]
        public MonitorSettingsConfig MonitorSettings { get; set; } = new MonitorSettingsConfig();

        public class ColorConfig
        {
            [JsonProperty("r")]
            public int R { get; set; }

            [JsonProperty("g")]
            public int G { get; set; }

            [JsonProperty("b")]
            public int B { get; set; }

            public Color ToDrawingColor()
            {
                return Color.FromArgb(R, G, B);
            }
        }

        public class MonitorSettingsConfig
        {
            [JsonProperty("useMonitorIndex")]
            public int UseMonitorIndex { get; set; } = 0; // Default to primary monitor

            [JsonProperty("captureRegion")]
            public CaptureRegionConfig CaptureRegion { get; set; } = new CaptureRegionConfig();

            public class CaptureRegionConfig
            {
                [JsonProperty("enabled")]
                public bool Enabled { get; set; } = false;

                [JsonProperty("x")]
                public int X { get; set; } = 0;

                [JsonProperty("y")]
                public int Y { get; set; } = 0;

                [JsonProperty("width")]
                public int Width { get; set; } = 1920;

                [JsonProperty("height")]
                public int Height { get; set; } = 1080;
            }
        }
        public static class AppSettings
        {
            public static LightingConfig Config { get; set; }
        }


        // Load configuration from file
        public static LightingConfig LoadFromFile(string filePath)
        {
            try
            {
                if (System.IO.File.Exists(filePath))
                {
                    string json = System.IO.File.ReadAllText(filePath);
                    var config = JsonConvert.DeserializeObject<LightingConfig>(json);

                    if (config != null)
                    {
                        Console.WriteLine($"Loaded config from {filePath}");

                        // Debug info for monitor settings
                        Console.WriteLine($"Monitor Index: {config.MonitorSettings.UseMonitorIndex}");
                        Console.WriteLine($"Capture Region Enabled: {config.MonitorSettings.CaptureRegion.Enabled}");

                        return config;
                    }
                }

               Console.WriteLine($"Config file not found or invalid. Using default settings.");
                return new LightingConfig();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error loading config: {ex.Message}");
                return new LightingConfig();
            }
        }

        // Save configuration to file
        public void SaveToFile(string filePath)
        {
            try
            {
                string json = JsonConvert.SerializeObject(this, Formatting.Indented);
                File.WriteAllText(filePath, json);
                Console.WriteLine($"Config saved to {filePath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error saving config: {ex.Message}");
            }
        }
    }
}