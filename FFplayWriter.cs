using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

public class FFplayWriter
{
    private ConcurrentQueue<byte[]> h264Queue = new ConcurrentQueue<byte[]>();
    private ConcurrentQueue<byte[]> h265Queue = new ConcurrentQueue<byte[]>();
    private Thread h264Thread;
    private Thread h265Thread;

    bool isSave = false;

    public FFplayWriter(Process ffplayProcessH264, Process ffplayProcessH265)
    {
        h264Thread = new Thread(() => ProcessQueue(h264Queue, ffplayProcessH264));
        h265Thread = new Thread(() => ProcessQueue(h265Queue, ffplayProcessH265));
        h264Thread.Start();
        h265Thread.Start();
    }

    public void WriteToFFplay(string format, byte[] data)
    {
        if (format == "h264")
        {
            h264Queue.Enqueue(data);
        }
        else if (format == "h265")
        {
            h265Queue.Enqueue(data);
        }
    }

    List<byte> buffer = new List<byte>();

    private void ProcessQueue(ConcurrentQueue<byte[]> queue, Process ffplayProcess)
    {
        while (true)
        {
            if (queue.TryDequeue(out byte[] data))
            {
                if (ffplayProcess != null && !ffplayProcess.HasExited)
                {
                    buffer.AddRange(data);

                    if (buffer.Count >= 2048)
                    {
                        if (isSave)
                        {
                            AppendBytesToFile("222.h265", buffer.ToArray());
                            buffer.Clear();
                        } 
                        else
                        {
                            try
                            {
                                ffplayProcess.StandardInput.BaseStream.Write(buffer.ToArray(), 0, buffer.Count);
                                ffplayProcess.StandardInput.BaseStream.Flush();
                                buffer.Clear();
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine("写入 ffplay 失败: " + ex.Message);
                            }
                        }
                       
                    }
                    
                }
            } else
            {
                Thread.Sleep(10); // 防止 CPU 占用过高
            }
           
        }
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
}