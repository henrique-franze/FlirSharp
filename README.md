# FlirSharp

[![.NET](https://img.shields.io/badge/.NET-9.0-blue.svg)](https://dotnet.microsoft.com/download/dotnet/9.0)

A C# library for extracting thermal data from FLIR JPEG images. This is a port of the Python [flyr](https://github.com/rlafuente/flyr) library to .NET.

## Features

- Extract 16-bit thermal data from FLIR JPEG images
- Convert raw sensor values to temperature (Celsius)
- Parse camera information and measurement data
- Support for both raw and PNG-compressed thermal data formats

## Quick Start

```csharp
using FlirSharp;

// Load and process a FLIR thermal image
var thermogram = new FlirThermogram("path/to/flir_image.jpg");

// Access thermal data as raw sensor values
ushort[,] rawData = thermogram.ThermalData;

// Access temperature data in Celsius
float[,] temperatures = thermogram.CelsiusData;

// Get camera information
var cameraInfo = thermogram.CameraInfo;
Console.WriteLine($"Emissivity: {cameraInfo["emissivity"]}");

// Access measurement annotations
foreach (var measurement in thermogram.Measurements)
{
    Console.WriteLine($"Measurement: {measurement.Label}");
}
```

## Current Status

**⚠️ Work in Progress**: This library is under active development. Current limitations:

- **PNG-compressed thermal data**: Detection implemented, but decompression requires completion of the SixLabors.ImageSharp integration
- **Camera information parsing**: Only minimal Planck parameters are extracted; full metadata parsing is incomplete
- **Measurement parsing**: Basic structure implemented, needs refinement

## Installation

### From Source
```bash
git clone https://github.com/your-username/FlirSharp.git
cd FlirSharp
dotnet build
```

### As a Library
The core functionality is in `FlirSharp.cs`. For library usage, consider creating a separate class library project:

```bash
dotnet new classlib -n FlirSharp
# Copy FlirSharp.cs to the new project
# Add SixLabors.ImageSharp package reference
```

## Contributing

Contributions are welcome! Priority areas:

1. **PNG decompression**: Complete the implementation in `ParseRawData()` method
2. **Camera info parsing**: Extend `ParseCameraInfo()` to extract all metadata fields
3. **Measurement refinement**: Improve measurement coordinate and parameter extraction
4. **Unit tests**: Add comprehensive test coverage
5. **Documentation**: Expand API documentation

## Related Projects

- [flyr](https://github.com/rlafuente/flyr) - The original Python library this project is based on
- [FlirImageExtractor](https://github.com/nationaldronesau/FlirImageExtractor) - Python tool for FLIR image processing

## License

This project is licensed under the same terms as the original flyr library. Please refer to the LICENSE file for details.

## Acknowledgments

This project is a port of the excellent [flyr](https://github.com/rlafuente/flyr) Python library by [rlafuente](https://github.com/rlafuente). All credit for the underlying algorithms and FLIR format understanding goes to the original authors.
