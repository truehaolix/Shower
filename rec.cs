using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Linq;
using System.Threading;
using System.Diagnostics;
using System.Collections.Generic;
using System.Threading.Tasks;

public class UDPDataReceiver
{
    private UdpClient udpClient;
    private string localIpAddress;
    private int port;
    private string h264FilePath;
    private string h265FilePath;
    private bool saveH264;
    private bool saveH265;
    private bool playH264;
    private bool playH265;
    private string videoFormat; // "h264" 或 "h265"

    private byte[] h264DataBuffer;
    private byte[] h265DataBuffer;
    private int receivedPacketCount;

    private IPEndPoint remoteEndPoint;

    private Process ffplayProcessH264;
    private Process ffplayProcessH265;

    private readonly object h264Lock = new object();
    private readonly object h265Lock = new object();

    private bool isReceiving;

    public UDPDataReceiver(string localIpAddress, int port, string videoFormat, string h264FilePath, string h265FilePath, bool saveH264, bool saveH265, bool playH264, bool playH265)
    {
        this.localIpAddress = localIpAddress;
        this.port = port;
        this.videoFormat = videoFormat.ToLower();
        this.h264FilePath = h264FilePath;
        this.h265FilePath = h265FilePath;
        this.saveH264 = saveH264;
        this.saveH265 = saveH265;
        this.playH264 = playH264;
        this.playH265 = playH265;

        // 验证视频格式
        if (this.videoFormat != "h264" && this.videoFormat != "h265")
        {
            throw new ArgumentException("视频格式必须是 'h264' 或 'h265'");
        }

        // 解析本地IP地址
        if (!IPAddress.TryParse(localIpAddress, out IPAddress localIPAddress))
        {
            throw new ArgumentException("无效的本地IP地址");
        }

        // 创建本地终结点并绑定UDP客户端
        IPEndPoint localEndPoint = new IPEndPoint(localIPAddress, port);
        udpClient = new UdpClient();

        // 设置套接字选项以允许地址重用
        udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udpClient.ExclusiveAddressUse = false;
        // 绑定到指定的本地IP地址和端口
        udpClient.Client.Bind(localEndPoint);

        // 启用广播接收
        udpClient.EnableBroadcast = true;
        IPAddress multicastAddress = IPAddress.Parse("226.0.0.22");
        udpClient.JoinMulticastGroup(multicastAddress);

        h264DataBuffer = new byte[0];
        h265DataBuffer = new byte[0];
    }

    // 异步接收函数
    public async Task StartReceivingAsync()
    {
        isReceiving = true;
        Console.WriteLine($"开始接收UDP数据，绑定到{localIpAddress}:{port}，视频格式: {videoFormat.ToUpper()}");
        using (FileStream fs = new FileStream("received_data.bin", FileMode.Append, FileAccess.Write))
        {
            // 根据视频格式启动 ffplay
            StartFFplay(videoFormat);

            // 异步接收数据
            try
            {
                while (isReceiving)
                {
                    // 异步接收数据包
                    byte[] receivedBytes = await ReceiveUdpPacketAsync();
                    if (receivedBytes != null)
                    {
                        // 将收到的数据写入文件
                        fs.Write(receivedBytes, 0, receivedBytes.Length);
                        fs.Flush(); // 刷新缓冲区
                        // 处理接收到的数据包
                        await ProcessReceivedPacketAsync(receivedBytes);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("接收过程中发生异常: " + ex.Message);
            }

            // 确保在接收停止后关闭 ffplay 进程
            StopFFplayProcesses();
        }
    }

    // 异步接收UDP数据包
    private async Task<byte[]> ReceiveUdpPacketAsync()
    {
        try
        {
            // 使用异步方法接收数据
            return await Task.Run(() => udpClient.Receive(ref remoteEndPoint));
        }
        catch (Exception ex)
        {
            Console.WriteLine("接收UDP数据包时发生异常: " + ex.Message);
            return null;
        }
    }

    // 异步处理接收到的视频数据包
    private async Task ProcessReceivedPacketAsync(byte[] packet)
    {
        try
        {
            if (packet.Length < 10) // 确保包长度足够
            {
                Console.WriteLine("接收到的数据包长度不足。");
                return;
            }

            // 检查包头是否有效
            if (packet[0] == 0xCD && packet[1] == 0xBC && packet[2] == 0xCF &&
                packet[3] == 0xF1 && packet[4] == 0xCA && packet[5] == 0xBC)
            {
                // 提取有效数据长度、包类型和包编号
                ushort dataLength = BitConverter.ToUInt16(packet, 6);
                int packetType = packet[8] & 0x03;
                byte packetNumber = packet[9];

                // 根据视频格式选择处理方法
                if (videoFormat.Equals("h264", StringComparison.OrdinalIgnoreCase))
                {
                    await ProcessH264PacketAsync( packetType,packet, dataLength);
                }
                else if (videoFormat.Equals("h265", StringComparison.OrdinalIgnoreCase))
                {
                    await ProcessH265PacketAsync(packetType, packet, dataLength);
                }
            }
            else
            {
                // 无效包头
                Console.WriteLine("无效的数据包头。");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("处理数据包时发生异常: " + ex.Message);
        }
    }

    // 异步处理H264数据包
    private async Task ProcessH265PacketAsync(int packetType, byte[] packet, ushort dataLength)
    {
        await Task.Delay(0); // 使用 await 来保持异步方法
        lock (h265Lock)
        {
            int headerSize = 6 + 2 + 1 + 1;
            int imageHeaderSize = 2 + 2 + 2 + 1 + 1 + 2 + 256;

            byte[] videoData = null;

            switch (packetType)
            {
                case 0x00: // 单包
                    if (packet.Length < headerSize + imageHeaderSize + 1)
                    {
                        Console.WriteLine("H265单包数据长度不足。");
                        return;
                    }
                    videoData = new byte[dataLength - headerSize - imageHeaderSize - 1];
                    Buffer.BlockCopy(packet, headerSize + imageHeaderSize, videoData, 0, videoData.Length);
                    break;

                case 0x01: // 起始包
                    h265DataBuffer = new byte[dataLength - headerSize - imageHeaderSize - 1];
                    Buffer.BlockCopy(packet, headerSize + imageHeaderSize, h265DataBuffer, 0, h265DataBuffer.Length);
                    receivedPacketCount = 1;
                    return;

                case 0x02: // 中间包
                    byte[] tempBuffer = new byte[h265DataBuffer.Length + (dataLength - headerSize - 1)];
                    Buffer.BlockCopy(h265DataBuffer, 0, tempBuffer, 0, h265DataBuffer.Length);
                    Buffer.BlockCopy(packet, headerSize, tempBuffer, h265DataBuffer.Length, dataLength - headerSize - 1);
                    h265DataBuffer = tempBuffer;
                    receivedPacketCount++;
                    return;

                case 0x03: // 结束包
                    tempBuffer = new byte[h265DataBuffer.Length + (dataLength - headerSize - 1)];
                    Buffer.BlockCopy(h265DataBuffer, 0, tempBuffer, 0, h265DataBuffer.Length);
                    Buffer.BlockCopy(packet, headerSize, tempBuffer, h265DataBuffer.Length, dataLength - headerSize - 1);
                    h265DataBuffer = tempBuffer;
                    videoData = h265DataBuffer;
                    break;

                default:
                    return;
            }

            // 如果视频数据已经收集完成，打印并保存
            if (videoData != null)
            {
                PrintH265VideoInfo(videoData);
                WriteToFFplay("h265", videoData);

                if (saveH265)
                {
                    SaveH265DataToFile(videoData);
                }

                // 重置缓冲区
                if (packetType == 0x03)
                {
                    h265DataBuffer = new byte[0];
                    receivedPacketCount = 0;
                }
            }
        }
    }

    private async Task ProcessH264PacketAsync(int packetType, byte[] packet, ushort dataLength)
    {
        await Task.Delay(0); // 使用 await 来保持异步方法
        lock (h264Lock)
        {
            int headerSize = 6 + 2 + 1 + 1;
            int imageHeaderSize = 2 + 2 + 2 + 1 + 1 + 2;

            byte[] videoData;

            if (packetType == 0x00) // 单包
            {
                if (packet.Length < headerSize + imageHeaderSize + 1)
                {
                    Console.WriteLine("H264单包数据长度不足。");
                    return;
                }
                videoData = packet.Skip(headerSize + imageHeaderSize).Take(dataLength - headerSize - imageHeaderSize - 1).ToArray();

                // 打印视频信息
                PrintH264VideoInfo(videoData);

                // 直接写入 ffplay
                WriteToFFplay("h264", videoData);

                // 保存到文件
                if (saveH264)
                {
                    SaveH264DataToFile(videoData);
                }
            }
            else if (packetType == 0x01) // 起始包
            {
                h264DataBuffer = packet.Skip(headerSize + imageHeaderSize).Take(dataLength - headerSize - imageHeaderSize - 1).ToArray();
                receivedPacketCount = 1;
            }
            else if (packetType == 0x02) // 中间包
            {
                byte[] tempBuffer = new byte[h264DataBuffer.Length + (dataLength - headerSize - 1)];
                Buffer.BlockCopy(h264DataBuffer, 0, tempBuffer, 0, h264DataBuffer.Length);
                Buffer.BlockCopy(packet, headerSize, tempBuffer, h264DataBuffer.Length, dataLength - headerSize - 1);
                h264DataBuffer = tempBuffer;
                receivedPacketCount++;
            }
            else if (packetType == 0x03) // 结束包
            {
                byte[] tempBuffer = new byte[h264DataBuffer.Length + (dataLength - headerSize - 1)];
                Buffer.BlockCopy(h264DataBuffer, 0, tempBuffer, 0, h264DataBuffer.Length);
                Buffer.BlockCopy(packet, headerSize, tempBuffer, h264DataBuffer.Length, dataLength - headerSize - 1);
                h264DataBuffer = tempBuffer;

                // 打印视频信息
                PrintH264VideoInfo(h264DataBuffer);

                // 将完整的H264数据写入 ffplay
                WriteToFFplay("h264", h264DataBuffer);

                // 保存到文件
                if (saveH264)
                {
                    SaveH264DataToFile(h264DataBuffer);
                }

                // 重置缓冲区
                h264DataBuffer = new byte[0];
                receivedPacketCount = 0;
            }
        }
    }


    private void PrintH264VideoInfo(byte[] videoData)
    {
        if (videoData.Length > 0)
        {
            byte nalUnitType = (byte)(videoData[4] & 0x1F); // NAL Unit Type (lowest 5 bits)
            Console.WriteLine($"NAL Unit Type: {nalUnitType}");

            // 输出帧类型
            switch (nalUnitType)
            {
                case 1: // Non-IDR frame
                    Console.WriteLine("Frame Type: P-Frame (Non-IDR)");
                    break;
                case 5: // IDR frame
                    Console.WriteLine("Frame Type: I-Frame (IDR)");
                    break;
                case 7: // SPS
                    Console.WriteLine("NAL Unit Type: Sequence Parameter Set (SPS)");
                    break;
                case 8: // PPS
                    Console.WriteLine("NAL Unit Type: Picture Parameter Set (PPS)");
                    break;
                default:
                    Console.WriteLine("Frame Type: Other NAL unit type");
                    break;
            }

            // 提取分辨率信息（假设SPS包含此信息）
            if (nalUnitType == 7)
            {
                int width = (videoData[10] << 8) + videoData[11];
                int height = (videoData[12] << 8) + videoData[13];

                Console.WriteLine($"Resolution: {width}x{height}");
            }
        }
    }

    private void PrintH265VideoInfo(byte[] videoData)
    {
        if (videoData.Length > 0)
        {
            byte nalUnitType = (byte)(videoData[4] & 0x3F); // H.265的NAL Unit类型（前6位）
            Console.WriteLine($"NAL Unit Type: {nalUnitType}");

            // 输出帧类型
            switch (nalUnitType)
            {
                case 1: // TRAIL_N (Non-I frame)
                    Console.WriteLine("Frame Type: P-Frame (Non-I)");
                    break;
                case 19: // IDR
                    Console.WriteLine("Frame Type: I-Frame (IDR)");
                    break;
                case 32: // VPS
                    Console.WriteLine("NAL Unit Type: Video Parameter Set (VPS)");
                    break;
                case 33: // SPS
                    Console.WriteLine("NAL Unit Type: Sequence Parameter Set (SPS)");
                    break;
                case 34: // PPS
                    Console.WriteLine("NAL Unit Type: Picture Parameter Set (PPS)");
                    break;
                default:
                    Console.WriteLine("Frame Type: Other NAL unit type");
                    break;
            }

            // 提取分辨率信息（假设SPS包含此信息）
            if (nalUnitType == 33)
            {
                // H.265分辨率通常存储在SPS中，解析逻辑与H.264类似
                int width = (videoData[12] << 8) + videoData[13];
                int height = (videoData[14] << 8) + videoData[15];

                Console.WriteLine($"Resolution: {width}x{height}");
            }
        }
    }

    private void StartFFplay(string format)
    {
        if (format == "h264" && ffplayProcessH264 == null && playH264)
        {
            ffplayProcessH264 = StartFFplayProcess(format);
            Console.WriteLine("已启动H264的ffplay.");
        }
        else if (format == "h265" && ffplayProcessH265 == null && playH265)
        {
            ffplayProcessH265 = StartFFplayProcess(format);
            Console.WriteLine("已启动H265的ffplay.");
        }

        writer = new FFplayWriter(ffplayProcessH264, ffplayProcessH265);
    }

    private Process StartFFplayProcess(string format)
    {
        ProcessStartInfo startInfo = new ProcessStartInfo
        {
            FileName = "ffplay.exe", // 确保 ffplay.exe 在系统 PATH 中或提供完整路径
            Arguments = format == "h264"
                ? "-f h264 -i pipe:0 -autoexit -fflags nobuffer -flags low_delay -strict experimental"
                : "-f hevc -i pipe:0 -autoexit -fflags nobuffer -flags low_delay -strict experimental",
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        try
        {
            Process process = new Process { StartInfo = startInfo };
            process.Start();
            return process;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"启动 ffplay 进程失败: {ex.Message}");
            return null;
        }
    }

    FFplayWriter writer;
    private void WriteToFFplay(string format, byte[] data)
    {

        if (format == "h264" && ffplayProcessH264 != null && !ffplayProcessH264.HasExited)
        {
            writer.WriteToFFplay("h264", data); // 将数据写入 H264 队列
            /*try
            {
                ffplayProcessH264.StandardInput.BaseStream.Write(data, 0, data.Length);
                ffplayProcessH264.StandardInput.BaseStream.Flush();
            }
            catch (Exception ex)
            {
                Console.WriteLine("写入 ffplay H264 失败: " + ex.Message);
            }*/
        }
        else if (format == "h265" && ffplayProcessH265 != null && !ffplayProcessH265.HasExited)
        {
            writer.WriteToFFplay("h265", data); // 将数据写入 H265 队列
            /*try
            {
                ffplayProcessH265.StandardInput.BaseStream.Write(data, 0, data.Length);
                ffplayProcessH265.StandardInput.BaseStream.Flush();
            }
            catch (Exception ex)
            {
                Console.WriteLine("写入 ffplay H265 失败: " + ex.Message);
            }*/
        }
    }

    private void StopFFplayProcesses()
    {
        if (ffplayProcessH264 != null && !ffplayProcessH264.HasExited)
        {
            try
            {
                ffplayProcessH264.StandardInput.Close();
                ffplayProcessH264.WaitForExit(1000);
                ffplayProcessH264.Close();
            }
            catch (Exception ex)
            {
                Console.WriteLine("关闭 ffplay H264 失败: " + ex.Message);
            }
        }

        if (ffplayProcessH265 != null && !ffplayProcessH265.HasExited)
        {
            try
            {
                ffplayProcessH265.StandardInput.Close();
                ffplayProcessH265.WaitForExit(1000);
                ffplayProcessH265.Close();
            }
            catch (Exception ex)
            {
                Console.WriteLine("关闭 ffplay H265 失败: " + ex.Message);
            }
        }
    }

    private void SaveH264DataToFile(byte[] videoData)
    {
        AppendBytesToFile(h264FilePath, videoData);
        Console.WriteLine("H264 视频已保存到 " + h264FilePath);
    }

    private void SaveH265DataToFile(byte[] videoData)
    {
        AppendBytesToFile(h265FilePath, videoData);
        Console.WriteLine("H265 视频已保存到 " + h265FilePath);
    }

    private void AppendBytesToFile(string filePath, byte[] data)
    {
        // 使用FileStream以追加模式打开文件
        using (FileStream fs = new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            // 使用BinaryWriter写入字节数组
            using (BinaryWriter writer = new BinaryWriter(fs))
            {
                writer.Write(data);
            }
        }
    }

    public void StopReceiving()
    {
        isReceiving = false;
        udpClient.Close();
        StopFFplayProcesses();
    }
}

// 示例用法
class Program
{
    static void PrintUsage()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  UDPDataReceiver.exe --ip <LocalIP> --port <Port> --format <h264|h265> [--save-h264 <H264FilePath>] [--save-h265 <H265FilePath>] [--play-h264] [--play-h265]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --ip <LocalIP>               本地IP地址，用于绑定网卡。");
        Console.WriteLine("  --port <Port>                UDP端口号。");
        Console.WriteLine("  --format <h264|h265>         指定视频格式。");
        Console.WriteLine("  --save-h264 <H264FilePath>   保存H264视频数据到指定文件。");
        Console.WriteLine("  --save-h265 <H265FilePath>   保存H265视频数据到指定文件。");
        Console.WriteLine("  --play-h264                  实时播放H264视频。");
        Console.WriteLine("  --play-h265                  实时播放H265视频。");
        Console.WriteLine("  --help                       显示此帮助信息。");
    }

    static async Task Main(string[] args)
    {
        if (args.Length == 0 || args.Contains("--help"))
        {
            PrintUsage();
            return;
        }

        string localIpAddress = null;
        int port = 0;
        string format = null;
        string h264FilePath = null;
        string h265FilePath = null;
        bool saveH264 = false;
        bool saveH265 = false;
        bool playH264 = false;
        bool playH265 = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--ip":
                    if (i + 1 < args.Length)
                    {
                        localIpAddress = args[++i];
                    }
                    else
                    {
                        Console.WriteLine("错误: --ip 需要一个参数。");
                        return;
                    }
                    break;
                case "--port":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out int parsedPort))
                    {
                        port = parsedPort;
                    }
                    else
                    {
                        Console.WriteLine("错误: --port 需要一个有效的整数参数。");
                        return;
                    }
                    break;
                case "--format":
                    if (i + 1 < args.Length)
                    {
                        format = args[++i].ToLower();
                        if (format != "h264" && format != "h265")
                        {
                            Console.WriteLine("错误: --format 需要 'h264' 或 'h265' 作为参数。");
                            return;
                        }
                    }
                    else
                    {
                        Console.WriteLine("错误: --format 需要一个参数。");
                        return;
                    }
                    break;
                case "--save-h264":
                    if (i + 1 < args.Length)
                    {
                        h264FilePath = args[++i];
                        saveH264 = true;
                    }
                    else
                    {
                        Console.WriteLine("错误: --save-h264 需要一个文件路径参数。");
                        return;
                    }
                    break;
                case "--save-h265":
                    if (i + 1 < args.Length)
                    {
                        h265FilePath = args[++i];
                        saveH265 = true;
                    }
                    else
                    {
                        Console.WriteLine("错误: --save-h265 需要一个文件路径参数。");
                        return;
                    }
                    break;
                case "--play-h264":
                    playH264 = true;
                    break;
                case "--play-h265":
                    playH265 = true;
                    break;
                default:
                    Console.WriteLine($"未知的参数: {args[i]}");
                    PrintUsage();
                    return;
            }
        }

        // 验证必要参数
        if (string.IsNullOrEmpty(localIpAddress) || port == 0 || string.IsNullOrEmpty(format))
        {
            Console.WriteLine("错误: --ip, --port 和 --format 是必需的参数。");
            PrintUsage();
            return;
        }

        // 如果选择保存但未指定文件路径，则报错
        if (saveH264 && string.IsNullOrEmpty(h264FilePath))
        {
            Console.WriteLine("错误: --save-h264 需要指定文件路径。");
            return;
        }

        if (saveH265 && string.IsNullOrEmpty(h265FilePath))
        {
            Console.WriteLine("错误: --save-h265 需要指定文件路径。");
            return;
        }

        // 根据指定的格式，启动相应的播放选项
        // 如果指定播放某种格式，但未指定保存路径，可以仅播放
        // 如果未指定播放，则仅保存（如果选择保存）

        // 如果指定的格式与播放选项不匹配，进行适当调整
        if (format == "h264" && !playH264)
        {
            playH265 = false; // 不播放H265
        }
        else if (format == "h265" && !playH265)
        {
            playH264 = false; // 不播放H264
        }

        UDPDataReceiver receiver = null;

        try
        {
            receiver = new UDPDataReceiver(localIpAddress, port, format, h264FilePath, h265FilePath, saveH264, saveH265, playH264, playH265);
        }
        catch (Exception ex)
        {
            Console.WriteLine("初始化UDPDataReceiver失败: " + ex.Message);
            return;
        }

        // 异步启动接收
        Task receivingTask = receiver.StartReceivingAsync();

        Console.WriteLine("按回车键停止接收...");
        Console.ReadLine();
        receiver.StopReceiving();

        // 等待接收任务完成
        await receivingTask;

        Console.WriteLine("接收结束。");
    }
}
