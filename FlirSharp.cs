using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Runtime.InteropServices;

namespace FlirSharp
{
    // Enum para tipos de ferramentas de medição
    public enum Tool
    {
        NONE = 0,
        SPOT = 1,
        AREA = 2,
        ELLIPSE = 3,
        LINE = 4,
        ENDPOINT = 5,
        ALARM = 6,
        UNUSED = 7,
        DIFFERENCE = 8
    }

    // Classe para representar uma medição
    public class Measurement
    {
        public Tool Tool { get; set; }
        public List<int> Params { get; set; } = new List<int>();
        public string Label { get; set; } = "";
    }

    // Enum para índices de records
    public enum RecordIndex
    {
        RAW_DATA = 1,
        CAMERA_INFO = 32,
        EMBEDDED_IMAGE = 14,
        PALETTE_INFO = 34,
        PICTURE_IN_PICTURE_INFO = 42,
        MEASUREMENT_INFO = 33
    }

    // Classe principal do termograma FLIR
    public class FlirThermogram
    {
        public string Path { get; set; } = "";
        public ushort[,] ThermalData { get; set; } = new ushort[0, 0];
        public float[,] CelsiusData { get; set; } = new float[0, 0];
        public Dictionary<string, float> CameraInfo { get; set; } = new Dictionary<string, float>();
        public List<Measurement> Measurements { get; set; } = new List<Measurement>();

        // TODO: DIMENSÕES - Padronizar convenção com Python
        // C# usa: [Height, Width] (convenção de matriz)
        // Python usa: (Width, Height) (convenção de imagem)
        // Considerar mudança futura para consistência
        public int Width { get; set; }
        public int Height { get; set; }

        // Converte dados térmicos brutos para Celsius
        public void ConvertToCelsius()
        {
            CelsiusData = new float[Height, Width];

            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    float temp = RawToKelvin(ThermalData[y, x]) - 273.15f;
                    CelsiusData[y, x] = temp;
                }
            }
        }

        // Converte valor bruto para Kelvin usando algoritmo FLIR
        private float RawToKelvin(ushort rawValue)
        {
            if (!CameraInfo.ContainsKey("planck_r1") || !CameraInfo.ContainsKey("planck_r2") ||
                !CameraInfo.ContainsKey("planck_b") || !CameraInfo.ContainsKey("planck_f") ||
                !CameraInfo.ContainsKey("planck_o"))
            {
                throw new InvalidOperationException(
                    "Camera info with Planck coefficients is required for temperature conversion. " +
                    "Camera info parsing is not yet implemented.");
            }

            float planck_r1 = CameraInfo["planck_r1"];
            float planck_r2 = CameraInfo["planck_r2"];
            float planck_b = CameraInfo["planck_b"];
            float planck_f = CameraInfo["planck_f"];
            float planck_o = CameraInfo["planck_o"];

            try
            {
                // FÓRMULA CORRETA FLIR:
                // raw_obj += planck_o
                // raw_obj *= planck_r2  
                // planck_term = planck_r1 / raw_obj + planck_f
                // temperature_kelvin = planck_b / log(planck_term)

                float raw_obj = rawValue;
                raw_obj += planck_o;  // Soma planck_o
                raw_obj *= planck_r2; // Multiplica por planck_r2
                float planck_term = planck_r1 / raw_obj + planck_f;

                if (planck_term <= 0)
                {
                    return 273.15f; // Retorna 0°C se der problema
                }

                float kelvin = planck_b / (float)Math.Log(planck_term);

                // Valida resultado
                if (float.IsNaN(kelvin) || float.IsInfinity(kelvin) || kelvin < 200 || kelvin > 400)
                {
                    return 273.15f + rawValue * 0.01f; // Fallback linear
                }

                return kelvin;
            }
            catch
            {
                return 273.15f + rawValue * 0.01f; // Fallback em caso de erro
            }
        }
    }

    // Parser principal para arquivos FLIR
    public class FlirParser
    {
        private const byte SEGMENT_SEP = 0xFF;
        private const byte APP1_MARKER = 0xE1;
        private readonly byte[] MAGIC_FLIR = { 0x46, 0x4C, 0x49, 0x52, 0x00 }; // "FLIR\0"

        public FlirThermogram Unpack(string filePath)
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException($"Arquivo não encontrado: {filePath}");

            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read);
            using var flirApp1Stream = ExtractFlirApp1(stream);

            var records = ParseFlirApp1(flirApp1Stream);
            var thermogram = ParseThermal(flirApp1Stream, records);
            thermogram.Path = filePath;

            return thermogram;
        }

        private MemoryStream ExtractFlirApp1(Stream stream)
        {
            // Verifica se é JPEG
            stream.Position = 0;
            var jpegHeader = new byte[2];
            stream.Read(jpegHeader, 0, 2);

            if (jpegHeader[0] != 0xFF || jpegHeader[1] != 0xD8)
                throw new InvalidDataException("Arquivo não é um JPEG válido");

            var chunks = new Dictionary<int, byte[]>();
            int? chunksCount = null;

            while (true)
            {
                int b = stream.ReadByte();
                if (b == -1) break;

                if (b != SEGMENT_SEP) continue;

                var parsedChunk = ParseFlirChunk(stream, chunksCount);
                if (parsedChunk == null) continue;

                chunksCount = parsedChunk.Value.chunksCount;
                int chunkNum = parsedChunk.Value.chunkNum;
                byte[] chunk = parsedChunk.Value.chunk;

                if (chunks.ContainsKey(chunkNum))
                    throw new InvalidDataException($"Chunk {chunkNum} duplicado");

                chunks[chunkNum] = chunk;

                if (chunkNum == chunksCount)
                    break;
            }

            if (chunksCount == null)
                throw new InvalidDataException("Nenhum chunk FLIR encontrado");

            // Concatena todos os chunks
            var flirApp1Bytes = new List<byte>();
            for (int i = 0; i <= chunksCount; i++)
            {
                if (chunks.ContainsKey(i))
                    flirApp1Bytes.AddRange(chunks[i]);
            }

            return new MemoryStream(flirApp1Bytes.ToArray());
        }

        private (int chunksCount, int chunkNum, byte[] chunk)? ParseFlirChunk(Stream stream, int? chunksCount)
        {
            var marker = new byte[1];
            if (stream.Read(marker, 0, 1) != 1 || marker[0] != APP1_MARKER)
            {
                stream.Position--;
                return null;
            }

            var lengthBytes = new byte[2];
            stream.Read(lengthBytes, 0, 2);
            int length = (lengthBytes[0] << 8) | lengthBytes[1];
            length -= 12; // Tamanho do header

            var magicFlir = new byte[5];
            stream.Read(magicFlir, 0, 5);

            if (!magicFlir.SequenceEqual(MAGIC_FLIR))
            {
                stream.Position -= 8;
                return null;
            }

            stream.Position++; // Pula 1 byte

            int chunkNum = stream.ReadByte();
            int chunksTot = stream.ReadByte();

            if (chunksCount == null)
                chunksCount = chunksTot;

            if (chunkNum < 0 || chunkNum > chunksTot || chunksTot != chunksCount)
                throw new InvalidDataException("Metadata de chunk inconsistente");

            var chunk = new byte[length + 1]; // +1 para correção do erro off-by-one
            int actualBytesRead = stream.Read(chunk, 0, length + 1);

            if (actualBytesRead != length + 1)
            {
                Array.Resize(ref chunk, actualBytesRead);
            }

            return (chunksTot, chunkNum, chunk);
        }

        private Dictionary<int, (int entry, int type, int offset, int length)> ParseFlirApp1(Stream stream)
        {
            stream.Position = 0;

            // Lê header do arquivo
            var fileFormatId = new byte[4];
            stream.Read(fileFormatId, 0, 4);

            stream.Position += 16; // Pula creator

            var versionBytes = new byte[4];
            stream.Read(versionBytes, 0, 4);

            var offsetBytes = new byte[4];
            stream.Read(offsetBytes, 0, 4);
            int recordDirOffset = BitConverter.ToInt32(offsetBytes.Reverse().ToArray(), 0);

            var countBytes = new byte[4];
            stream.Read(countBytes, 0, 4);
            int recordDirCount = BitConverter.ToInt32(countBytes.Reverse().ToArray(), 0);

            // Vai para o diretório de records
            stream.Position = recordDirOffset;

            var records = new Dictionary<int, (int entry, int type, int offset, int length)>();

            for (int i = 0; i < recordDirCount; i++)
            {
                var recordMetadata = ParseFlirRecordMetadata(stream, i);
                if (recordMetadata.HasValue)
                {
                    var (entry, type, offset, length) = recordMetadata.Value;
                    records[type] = (entry, type, offset, length);
                }
            }

            return records;
        }

        private (int entry, int type, int offset, int length)? ParseFlirRecordMetadata(Stream stream, int recordNr)
        {
            int entry = recordNr * 32;
            stream.Position = entry;

            var typeBytes = new byte[2];
            stream.Read(typeBytes, 0, 2);
            int recordType = (typeBytes[0] << 8) | typeBytes[1];

            if (recordType < 1) return null;

            stream.Position += 2; // Pula subtype
            stream.Position += 4; // Pula version
            stream.Position += 4; // Pula index id

            var offsetBytes = new byte[4];
            stream.Read(offsetBytes, 0, 4);
            int recordOffset = BitConverter.ToInt32(offsetBytes.Reverse().ToArray(), 0);

            var lengthBytes = new byte[4];
            stream.Read(lengthBytes, 0, 4);
            int recordLength = BitConverter.ToInt32(lengthBytes.Reverse().ToArray(), 0);

            return (entry, recordType, recordOffset, recordLength);
        }

        private FlirThermogram ParseThermal(Stream stream, Dictionary<int, (int entry, int type, int offset, int length)> records)
        {
            var thermogram = new FlirThermogram();

            // Parse raw data
            if (records.ContainsKey((int)RecordIndex.RAW_DATA))
            {
                var (width, height, thermalData) = ParseRawData(stream, records[(int)RecordIndex.RAW_DATA]);
                thermogram.Width = width;
                thermogram.Height = height;
                thermogram.ThermalData = thermalData;
            }

            // Parse camera info
            if (records.ContainsKey((int)RecordIndex.CAMERA_INFO))
            {
                thermogram.CameraInfo = ParseCameraInfo(stream, records[(int)RecordIndex.CAMERA_INFO]);
            }

            // Parse measurements
            if (records.ContainsKey((int)RecordIndex.MEASUREMENT_INFO))
            {
                thermogram.Measurements = ParseMeasurements(stream, records[(int)RecordIndex.MEASUREMENT_INFO]);
            }

            // Converte para Celsius
            thermogram.ConvertToCelsius();

            return thermogram;
        }

        private (int width, int height, ushort[,] thermalData) ParseRawData(Stream stream, (int entry, int type, int offset, int length) metadata)
        {
            // Analisa os dados para detectar se é PNG
            stream.Position = metadata.offset + 32;
            var headerBytes = new byte[100]; // Lê mais bytes para encontrar IHDR
            stream.Read(headerBytes, 0, headerBytes.Length);

            // Procura por assinatura PNG (pode estar truncada no FLIR)
            bool isPng = false;
            for (int i = 0; i < headerBytes.Length - 4; i++)
            {
                if (headerBytes[i] == 0x89 && headerBytes[i + 1] == 0x50 &&
                    headerBytes[i + 2] == 0x4E && headerBytes[i + 3] == 0x47)
                {
                    isPng = true;
                    break;
                }
                // FLIR PNG truncado começando com "NG": 4E 47
                else if (headerBytes[i] == 0x4E && headerBytes[i + 1] == 0x47)
                {
                    isPng = true;
                    break;
                }
            }

            // Ou procura por IHDR (header do PNG)
            if (!isPng)
            {
                for (int i = 0; i < headerBytes.Length - 4; i++)
                {
                    if (headerBytes[i] == 0x49 && headerBytes[i + 1] == 0x48 &&
                        headerBytes[i + 2] == 0x44 && headerBytes[i + 3] == 0x52)
                    {
                        isPng = true;
                        break;
                    }
                }
            }

            int width = 0, height = 0;

            if (isPng)
            {
                // Para PNG, extrai dimensões do header IHDR
                // IHDR: 4 bytes length + 4 bytes "IHDR" + 4 bytes width + 4 bytes height
                for (int i = 0; i < headerBytes.Length - 12; i++)
                {
                    if (headerBytes[i] == 0x49 && headerBytes[i + 1] == 0x48 &&
                        headerBytes[i + 2] == 0x44 && headerBytes[i + 3] == 0x52)
                    {
                        // Encontrou IHDR, extrai dimensões (big-endian)
                        if (i + 11 < headerBytes.Length)
                        {
                            width = (headerBytes[i + 4] << 24) | (headerBytes[i + 5] << 16) |
                                   (headerBytes[i + 6] << 8) | headerBytes[i + 7];
                            height = (headerBytes[i + 8] << 24) | (headerBytes[i + 9] << 16) |
                                    (headerBytes[i + 10] << 8) | headerBytes[i + 11];
                            break;
                        }
                    }
                }
            }
            else
            {
                // Para dados não-PNG, lê dimensões do header (offset +2)
                stream.Position = metadata.offset + 2;
                var dimensionBytes = new byte[4];
                stream.Read(dimensionBytes, 0, 4);
                
                width = BitConverter.ToUInt16(dimensionBytes, 0);
                height = BitConverter.ToUInt16(dimensionBytes, 2);
            }

            if (width == 0 || height == 0)
            {
                throw new InvalidDataException($"Could not determine thermal image dimensions from metadata");
            }

            var thermalData = new ushort[height, width];

            // Lê os dados térmicos do offset 
            stream.Position = metadata.offset + 32;
            int dataLength = metadata.length - 32;
            var buffer = new byte[dataLength];
            stream.Read(buffer, 0, buffer.Length);

            // Verifica se é PNG - FLIR pode ter PNG truncado começando com "NG"
            bool isPngData = false;
            if (buffer.Length >= 4)
            {
                // Assinatura PNG completa: 89 50 4E 47
                if (buffer[0] == 0x89 && buffer[1] == 0x50 && buffer[2] == 0x4E && buffer[3] == 0x47)
                {
                    isPngData = true;
                }
                // FLIR PNG truncado começando com "NG": 4E 47
                else if (buffer[0] == 0x4E && buffer[1] == 0x47)
                {
                    isPngData = true;
                }
            }

            if (isPngData)
            {
                // TODO: CRÍTICO - Implementar descompressão PNG
                // Os dados térmicos FLIR estão comprimidos em PNG!
                // Precisa usar biblioteca de imagem para:
                // 1. Descomprimir PNG -> obter dados 16-bit reais
                // 2. Corrigir byte order: (x >> 8) + ((x & 0x00FF) << 8)
                // 3. Então converter para temperaturas

                // Por enquanto, deixa zeros para não crashar
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        thermalData[y, x] = 0; // Placeholder até implementar PNG
                    }
                }
            }
            else
            {
                Console.WriteLine($"    Debug: Processing raw thermal data (non-PNG)");
                // Dados térmicos brutos (não PNG)
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        int index = (y * width + x) * 2;
                        if (index + 1 < buffer.Length)
                        {
                            // Little endian
                            ushort value = (ushort)(buffer[index] | (buffer[index + 1] << 8));
                            thermalData[y, x] = value;
                        }
                    }
                }
            }

            return (width, height, thermalData);
        }

        private Dictionary<string, float> ParseCameraInfo(Stream stream, (int entry, int type, int offset, int length) metadata)
        {
            var cameraInfo = new Dictionary<string, float>();

            // TODO: Implementar parsing completo do camera info
            // Camera info parsing is not yet implemented.
            // The binary format contains camera parameters like emissivity, reflection temperature,
            // atmospheric temperature, distance, humidity, and Planck coefficients for temperature conversion.
            // See Python flyr library for reference implementation.

            throw new NotImplementedException(
                "Camera info parsing is not yet implemented. " +
                "Temperature conversion requires Planck coefficients from camera metadata. " +
                "Contributions welcome - see README.md for details.");
        }

        private List<Measurement> ParseMeasurements(Stream stream, (int entry, int type, int offset, int length) metadata)
        {
            stream.Position = metadata.offset;
            var measurementBytes = new byte[metadata.length];
            stream.Read(measurementBytes, 0, measurementBytes.Length);

            var measurementStream = new MemoryStream(measurementBytes);
            measurementStream.Position = 12; // Pula primeiros bytes

            var measurements = new List<Measurement>();
            int measurementIndex = 0;

            if (measurementBytes.Length < 11)
            {
                return measurements;
            }

            int measurementLength = measurementBytes[10] - 1;
            measurementStream.Position = 11;

            while (measurementLength > 0 && measurementStream.Position < measurementStream.Length)
            {
                if (measurementStream.Position + measurementLength > measurementStream.Length)
                {
                    break;
                }

                var measurementData = new byte[measurementLength];
                int bytesRead = measurementStream.Read(measurementData, 0, measurementLength);

                if (bytesRead != measurementLength)
                {
                    break;
                }

                var measurement = ParseMeasurement(measurementData, measurementIndex);
                if (measurement != null)
                {
                    measurements.Add(measurement);
                    measurementIndex++;
                }

                // Lê próximo tamanho de medição
                if (measurementStream.Position >= measurementStream.Length)
                {
                    break;
                }

                int nextLengthByte = measurementStream.ReadByte();
                if (nextLengthByte == -1)
                {
                    break;
                }

                measurementLength = nextLengthByte - 1;
            }

            return measurements;
        }

        private Measurement? ParseMeasurement(byte[] data, int measurementIndex)
        {
            if (data.Length < 10) return null;

            var stream = new MemoryStream(data);
            stream.Position = 3;

            int numBytesParams = stream.ReadByte();
            var labelLengthBytes = new byte[2];
            stream.Read(labelLengthBytes, 0, 2);
            int numBytesLabel = (labelLengthBytes[0] << 8) | labelLengthBytes[1];

            stream.Position += 3;
            int tool = stream.ReadByte();

            stream.Position += 24; // Pula para parâmetros

            var params_ = new List<int>();
            while (params_.Count * 2 < numBytesParams)
            {
                var paramBytes = new byte[2];
                if (stream.Read(paramBytes, 0, 2) != 2) break;
                int param = (paramBytes[0] << 8) | paramBytes[1];
                params_.Add(param);
            }

            // Correção do bug de coordenadas Y para medições não-primárias
            if (measurementIndex > 0 && params_.Count >= 2)
            {
                var toolEnum = (Tool)tool;
                if (toolEnum == Tool.SPOT || toolEnum == Tool.AREA || toolEnum == Tool.ELLIPSE || toolEnum == Tool.LINE)
                {
                    params_[1] += 256; // Corrige coordenada Y

                    if (toolEnum == Tool.ELLIPSE && params_.Count >= 4)
                        params_[3] += 256;
                    else if (toolEnum == Tool.LINE && params_.Count >= 4)
                        params_[3] += 256;
                }
            }

            // Lê label
            string label = "";
            if (numBytesLabel > 0)
            {
                var labelBytes = new byte[numBytesLabel];
                stream.Read(labelBytes, 0, numBytesLabel);
                label = Encoding.BigEndianUnicode.GetString(labelBytes).TrimEnd('\0');
            }

            if (!Enum.IsDefined(typeof(Tool), tool))
                return null;

            return new Measurement
            {
                Tool = (Tool)tool,
                Params = params_,
                Label = label
            };
        }
    }
}
