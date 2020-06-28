/*
 * U64launcher
 *
 * CLI tool to send Commodore 64 prg(or d64) files Ultimate 64 over LAN
 *  
 * Version 1.0 (See CHANGELOG)
  *  
 * Written & released by M.F. Wieland (TSB)
 *  
 *  Licensed under the MIT License. See LICENSE file in the project root for full license information.  
 */

 using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace u64launcher
{
    class Program
    {

        static void U64_run_prg(string f_name, string ip_address, int tcp_port)
        {
            long fsize = new System.IO.FileInfo(f_name).Length;
            string[] f_name_split = f_name.Split('.');

            string f_ext = f_name_split[f_name_split.Length - 1];

            Byte[] msg_run_header;
            Byte[] file_buf;
            Byte[] msg_run_cmd;

            if (f_ext.ToLower() == "prg")
            {
                msg_run_header = new byte[] { 0x02, 0xff, (byte)(fsize & 0xFF), (byte)((fsize & 0xFF00) >> 8) };
            }
            else
            if (f_ext.ToLower() == "d64")
            {
                // mount and run
                msg_run_header = new byte[] { 0x0b, 0xff, (byte)(fsize & 0xFF), (byte)((fsize & 0xFF00) >> 8), (byte)((fsize & 0xFF0000) >> 16) };
            }
            else
            {
                Console.WriteLine( "File not supported.\nOnly PRG & D64 files are supported.");

                return;
            }


            // Create tcpclient
            TcpClient client = new TcpClient(ip_address, tcp_port);
            // Create client stream
            NetworkStream stream = client.GetStream();

            // Make SOCKET_CMD

            file_buf = File.ReadAllBytes(f_name);
            msg_run_cmd = new Byte[msg_run_header.Length + file_buf.Length];

            Array.Copy(msg_run_header, msg_run_cmd, msg_run_header.Length);
            Array.Copy(file_buf, 0, msg_run_cmd, msg_run_header.Length, file_buf.Length);

            // Create & send message to start video stream
            stream.Write(msg_run_cmd, 0, msg_run_cmd.Length);

            // Close everything.
            stream.Close();
            client.Close();
        }

        static void Main(string[] args)
        {
            Console.WriteLine("U64Launcher v1.0 by M.F. Wieland (TSB)\n");

            if (args.Length != 2 )
            {
                Console.WriteLine("Usage: u64launcher filename ip-adres");
                Console.WriteLine("Example: u64launcher autorun.prg 192.168.2.64");
            }
            else
            {
                int tcp_port = 64;
                if (args.Length>2)
                {
                    tcp_port = (args[2] != "") ? int.Parse(args[2]) : 64;
                }
                U64_run_prg(args[0], args[1], tcp_port);
            }
        }
    }
}
