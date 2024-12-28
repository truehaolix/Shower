using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;

internal class Program1
{
    static void Main(string[] args)
    {
        string inputFilePath = "D:/prj/miss/20241122/shooter/Assets/StreamingAssets/1.mp4"; // 输入文件路径
        string codec = "libx264"; // H.264 编码器（可改为 "libx265"）

        // 设置 UDP 广播地址和端口
        string udpAddress = "239.255.255.255";
        int udpPort = 1234;

        // 创建 UDP 客户端
        UdpClient udpClient = new UdpClient();
        udpClient.JoinMulticastGroup(IPAddress.Parse(udpAddress));

        // 启动 FFmpeg
        Process ffmpeg = new Process();
        ffmpeg.StartInfo.FileName = "ffmpeg";
        ffmpeg.StartInfo.Arguments = $"-re -i {inputFilePath} -c:v {codec} -preset fast -b:v 1M -fflags nobuffer -flush_packets 1 -f h264 -";
        ffmpeg.StartInfo.UseShellExecute = false;
        ffmpeg.StartInfo.RedirectStandardOutput = true;
        ffmpeg.StartInfo.RedirectStandardError = true;
        ffmpeg.StartInfo.CreateNoWindow = true;

        try
        {
            ffmpeg.Start();
            Stream ffmpegOutput = ffmpeg.StandardOutput.BaseStream;

            byte[] buffer = new byte[4096];
            int bytesRead;

            Console.WriteLine("Streaming started...");

            while ((bytesRead = ffmpegOutput.Read(buffer, 0, buffer.Length)) > 0)
            {
                // 通过 UDP 发送读取的数据块
                Console.WriteLine("Streaming bytesRead." + bytesRead);
                udpClient.Send(buffer, bytesRead, udpAddress, udpPort);
            }

            Console.WriteLine("Streaming ended.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
        }
        finally
        {
            udpClient.Close();
            ffmpeg.Close();
        }
    }
}
