using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FlirSharp
{
    /// <summary>
    /// Demo application showing how to use the FlirSharp library to extract thermal data from FLIR images.
    /// </summary>
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("FlirSharp Demo - FLIR Thermal Image Parser");
            Console.WriteLine("==========================================");

            string inputFolder = "input_images";

            if (!Directory.Exists(inputFolder))
            {
                Console.WriteLine($"❌ Folder '{inputFolder}' not found!");
                Console.WriteLine("Please create the folder and add some FLIR JPEG images to test.");
                return;
            }

            var imageFiles = Directory.GetFiles(inputFolder, "*.jpg", SearchOption.TopDirectoryOnly)
                .Concat(Directory.GetFiles(inputFolder, "*.jpeg", SearchOption.TopDirectoryOnly))
                .ToList();

            if (imageFiles.Count == 0)
            {
                Console.WriteLine($"❌ No JPEG files found in '{inputFolder}' folder.");
                Console.WriteLine("Please add some FLIR JPEG images to test.");
                return;
            }

            Console.WriteLine($"📁 Found {imageFiles.Count} image file(s)");
            Console.WriteLine();

            var parser = new FlirParser();

            foreach (var imgPath in imageFiles)
            {
                string fileName = Path.GetFileName(imgPath);
                Console.WriteLine($"📄 Processing: {fileName}");

                try
                {
                    // Parse the FLIR image to extract thermal data
                    var thermogram = parser.Unpack(imgPath);

                    Console.WriteLine($"  ✅ Successfully loaded FLIR image");
                    Console.WriteLine($"  📏 Thermal data dimensions: {thermogram.Height} x {thermogram.Width}");
                    Console.WriteLine($"  📊 Measurements found: {thermogram.Measurements.Count}");

                    // Try to convert to temperature (will fail until camera info parsing is implemented)
                    try
                    {
                        thermogram.ConvertToCelsius();
                        Console.WriteLine($"  🌡️  Temperature conversion: SUCCESS");

                        // Show temperature statistics
                        ShowTemperatureStats(thermogram);
                    }
                    catch (Exception tempEx)
                    {
                        Console.WriteLine($"  ⚠️  Temperature conversion: NOT AVAILABLE");
                        Console.WriteLine($"      Reason: {tempEx.Message}");
                    }

                    // Show measurement details
                    ShowMeasurements(thermogram);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  ❌ Error processing image: {ex.Message}");
                }

                Console.WriteLine();
            }

            Console.WriteLine("Demo completed. See README.md for library usage and contribution opportunities.");
        }

        private static void ShowTemperatureStats(FlirThermogram thermogram)
        {
            float minTemp = float.MaxValue;
            float maxTemp = float.MinValue;
            float sumTemp = 0;
            int count = 0;

            for (int y = 0; y < thermogram.Height; y++)
            {
                for (int x = 0; x < thermogram.Width; x++)
                {
                    float temp = thermogram.CelsiusData[y, x];
                    minTemp = Math.Min(minTemp, temp);
                    maxTemp = Math.Max(maxTemp, temp);
                    sumTemp += temp;
                    count++;
                }
            }

            float avgTemp = sumTemp / count;
            Console.WriteLine($"      Min: {minTemp:F1}°C | Max: {maxTemp:F1}°C | Avg: {avgTemp:F1}°C");
        }

        private static void ShowMeasurements(FlirThermogram thermogram)
        {
            if (thermogram.Measurements.Count == 0)
            {
                Console.WriteLine($"  📏 No measurement annotations found");
                return;
            }

            for (int i = 0; i < thermogram.Measurements.Count; i++)
            {
                var measurement = thermogram.Measurements[i];
                Console.WriteLine($"      [{i}] {measurement.Tool}: {measurement.Label}");

                if (measurement.Params.Count > 0)
                {
                    Console.WriteLine($"          Parameters: [{string.Join(", ", measurement.Params)}]");
                }
            }
        }
    }
}
