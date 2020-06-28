/*
 * U64 Streamer - Stream parser/viewer for U64 video stream
 * Code written by M.F. Wieland (TSB) 2019-2020
 * 
 */

using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using DirectBitmap_class;
using NAudio.Wave;
using u64streamclient;

namespace u64streamer_application
{

    public partial class frm_main : Form
    {
        public static frm_main main_frm;

        public Color[] active_colors = new Color[16];

        // Stream/UDP vars
        public struct u64_stream_udp_header
        {
            public ushort sequence_number;
            public ushort frame_number;
            public ushort line_number;
            public ushort pixels_per_line;
            public byte bits_per_pixel;
            public byte lines_per_packet;
            public ushort reserved;
        }

        // setup forms
        private frm_menu frmmenu = new frm_menu();
        public frm_settings frmsSettings = new frm_settings();
        public frm_color_selector frmColorSelector = new frm_color_selector();
        private frm_credits frmcredits = new frm_credits();
        private frm_stats frmdiag = new frm_stats();
        public frm_about frmabout = new frm_about();
        private frm_dialog_saveimage frmDialogSaveImage = new frm_dialog_saveimage();
        public frm_video_recorder_msg frmVideoRecorderMsg = new frm_video_recorder_msg();

        private string copyrightSuffix = "";
        private Size frm_border_size = new Size();

        // CTRL+C control - copy image with ctrl+c to clipboard
        public bool image_ctrl_c = false;

        // Bitmap vars / const 
        const int g_height_p = 272; // pal height
        const int g_height_n = 240; // nstc height
        const int g_width = 384;

        /*
         * Video mode
         * 0 = PAL (default)
         * 1 = NTSC
         */
        private int video_mode = 0;

        private int imWidth = 384;
        public int imHeight = 272;
        private byte[] rawImage;
        private decimal imRatio;

        // Stream control
        private bool stream_active = false;

        // Audio stream control
        private static int audio_listen_port = 11001;
        private UdpClient audio_listener;
        byte[] audio_receive_byte_array;
        private bool audio_stream_active = false;
        private long audio_received_bytes = 0;
        private long audio_sample_cnt = 0;
        private UInt16 SampleRate = 48000;//47983; // PAL
        private bool _video_mode_change_restart = false;

        private int audio_output_device = 2;
        private WaveOut waveOut;
        private DirectSoundOut directSoundOut;
        private WasapiOut wasapiOut;

        // Audio stream writer
        private byte[] audio_writer_buffer = new byte[768];
        private static WaveFormat waveFormat;// = new WaveFormat(48000, 16, 2);
        private WaveFileWriter audio_writer;// = new WaveFileWriter("test.wav", waveFormat);
        public bool audio_stream_rec_active = false;

        // Video stream control
        private static int video_listen_port = 11000;
        private UdpClient video_listener;
        private u64_stream_udp_header udp_header;

        private bool video_stream_active = false;
        private long video_received_bytes = 0;

        private int frame_buffer_count = 8;
        private UInt32 frame_buffer_pos = 0;
        private DirectBitmap[] frame_buffer;

        private Bitmap logo;

        private long fps = 0;
        private long video_packet_loss = 0;
        private long video_frame_cnt = 0;

        public int detect_video_mode = 0;

        // video recorder
        //private Bitmap _videowriter_frame_buffer;
        //private bool _videowriter_frame_available = false;

        public void set_screen_mode()
        {
            // Set Height to PAL or NTSC, based on setting
            if (video_mode == 0)
            {
                imHeight = g_height_p;
            }
            else
            {
                imHeight = g_height_n;
            }

            // calc ratio
            imRatio = (decimal)imWidth / (decimal)imHeight;

            // Set height
            this.Height = (imHeight * 2) + frm_border_size.Height;
            this.imageOutput.Height = (imHeight * 2);


            // set menu location to bottom of window
            frmmenu.Location = new Point(this.Left, this.Bottom); // or any value to set the location
        }
            
        public void set_active_colors()
        {
            // set active color table
            for (int i = 0; i <= 15; i++)
            {
                active_colors[i] = frmsSettings.color_palette[i];
            }
        }

        private void delay( int msec )
        {
            var t = Task.Run(async delegate
            {
                await Task.Delay(msec);
                return 42;
            });

            t.Wait();
        }

        public frm_main()
        {
            InitializeComponent();

            // load / set default settings
            if (!File.Exists(@"settings.xml"))
            {
                // set defaults
                frmsSettings.video_codec = "Default";
                frmsSettings.video_size = 1;
                frmsSettings.image_size = 1;
                frmsSettings.frame_buffer_count = 8;
                frmsSettings.audio_output_device = 2;

                // set colors;
                for (int i = 0; i <= 15; i++)
                {
                    frmsSettings.color_palette[i] = frmsSettings.u64colors[i];
                }

                frmsSettings.save_settings();
            }
            else
            {
                // load settings
                frmsSettings.load_settings();

                video_listen_port = frmsSettings.video_listen_port;
                audio_listen_port= frmsSettings.audio_listen_port;

                frame_buffer_count = frmsSettings.frame_buffer_count + 1;

                audio_output_device = frmsSettings.audio_output_device;
            }

            // set active color table
            set_active_colors();
        }

        private void Form1_Shown(object sender, EventArgs e)
        {
            // create capture directory
            if (!Directory.Exists("capture"))
            {
                Directory.CreateDirectory("capture");
            }

            // set version number
            string version = Assembly.GetExecutingAssembly().GetName().Version.Major.ToString() + "." + Assembly.GetExecutingAssembly().GetName().Version.Minor.ToString() + copyrightSuffix;
            frmmenu.lbCopyright.Text += version;
            frmabout.lb_copyright.Text = "version" + version;

            // form sizing
            frm_border_size.Width = this.Width - imageOutput.Width; 
            frm_border_size.Height = this.Height - imageOutput.Height;

            // show menu
            frmmenu.Width = this.Width;
            frmmenu.Location = new Point(this.Left, this.Bottom); 
            frmmenu.Show(this);

            logo = new Bitmap(imageOutput.Image);

            stream_control();
        }

        private void frm_main_FormClosed(object sender, FormClosedEventArgs e)
        {
            if (stream_active)
            {
                stream_control();
            }
        }

        public void showcredits()
        {
            frmcredits.Show(this);
        }

        public void showstats()
        {
            frmdiag.Show();
        }


        private void frm_main_Resize(object sender, EventArgs e)
        {
            // resizeWindow();
        }

        private void video_stream_worker_DoWork(object sender, System.ComponentModel.DoWorkEventArgs e)
        {

            // set stream active
            video_stream_active = true;

            // setup udp listener
            video_listener = new UdpClient(video_listen_port);
            IPEndPoint groupEP = new IPEndPoint(IPAddress.Any, video_listen_port);

            // setup raw buffer and direct bitmap drawing canvas
            // set to max size imWidth * pal_h
            rawImage = new byte[(imWidth / 2) * 272]; //imHeight

            // setup buffer array
            frame_buffer = new DirectBitmap[frame_buffer_count];
            for (int i=0;i< frame_buffer_count; i++)
            {
                frame_buffer[i] = new DirectBitmap(imWidth, imHeight);
            }

            // setup buffers & image vars
            byte[] receive_byte_array;

            int stream_img_width = imWidth / 2;


            // setup video frame buffer
            try
            {
                DirectBitmap _videowriter_frame_buffer_tmp = new DirectBitmap(imWidth, imHeight);
            }
            catch(Exception)
            {
                //MessageBox.Show(ex.ToString(),"Setup buffers");
            }
            
            // start stream processing
            frame_buffer_pos = 0;
            do
            {
                if (!_video_mode_change_restart)
                {
                    // start receive frames
                    int bufpos = 0;
                    try
                    {
                        var watch = System.Diagnostics.Stopwatch.StartNew();

                        // wait for first frame..
                        // loop until bit 15 is set on line_number
                        int prev_line_num = 0;
                        do
                        {
                            receive_byte_array = video_listener.Receive(ref groupEP);

                            // update received bytes
                            video_received_bytes += receive_byte_array.Length;

                            // parse udp header
                            udp_header.sequence_number = BitConverter.ToUInt16(receive_byte_array, 0);
                            udp_header.frame_number = BitConverter.ToUInt16(receive_byte_array, 2);

                            udp_header.line_number = BitConverter.ToUInt16(receive_byte_array, 4);
                            udp_header.pixels_per_line = BitConverter.ToUInt16(receive_byte_array, 6);
                            udp_header.bits_per_pixel = receive_byte_array[8];
                            udp_header.lines_per_packet = receive_byte_array[9];

                            if (udp_header.line_number > 0 && (prev_line_num + 4 > udp_header.line_number))
                            {
                                video_packet_loss++;
                            }
                            prev_line_num = udp_header.line_number;

                            // calc bufpos
                            bufpos = (udp_header.line_number & 0xfff) * ((udp_header.pixels_per_line * udp_header.bits_per_pixel) / 8);

                            // copy bitmap data to rawImage buffer
                            try
                            {
                                Array.Copy(receive_byte_array, 12, rawImage, bufpos, receive_byte_array.Length - 12);
                            }
                            catch (Exception)
                            {
                               // Console.Write("Error copying received lines to rawImage buffer.");
                            }

                        } while ((udp_header.line_number & 0x8000) != 0x8000);

                        if ((udp_header.line_number + 4 & 0xfff) != imHeight)
                        {
                            // detected PAL mode
                            if ((udp_header.line_number + 4 & 0xfff) == g_height_p)
                            {
                                video_mode = 0;

                                _video_mode_change_restart = true;
                                stream_control();
                            }
                            // detected NTSC mode
                            else
                            {
                                video_mode = 1;
                                _video_mode_change_restart = true;
                                stream_control();
                            }
                        }

                        video_frame_cnt++;

                        if(!_video_mode_change_restart)
                        {
                            // Draw received frame (rawImage) into bitmap buffer
                            Int32 x = 0;
                            Int32 y = 0;
                            Int32 bufferpos = 0;
                            try
                            {
                                while (y < imHeight)
                                {
                                    while (x < imWidth - 1)
                                    {
                                        // get pixeldata for pixel 1 & 2
                                        frame_buffer[(frame_buffer_pos) % frame_buffer_count].SetPixel(x, y, active_colors[rawImage[bufferpos] & 0xf]);
                                        frame_buffer[(frame_buffer_pos) % frame_buffer_count].SetPixel(x + 1, y, active_colors[(rawImage[bufferpos]) >> 4]);

                                        // get pixeldata for pixel 1 & 2
                                        //_videowriter_frame_buffer_tmp.SetPixel(x, y, c64colors[rawImage[bufferpos] & 0xf]);
                                        //_videowriter_frame_buffer_tmp.SetPixel(x + 1, y, c64colors[(rawImage[bufferpos]) >> 4]);

                                        x += 2;
                                        bufferpos++;
                                    }
                                    // set new position..
                                    y++;
                                    x = 0;
                                }
                            }
                            catch (Exception)
                            {
                                // System.Windows.MessageBox.Show("Oeps.. something went wrong while reading bitmap data..");
                            }
                            frame_buffer_pos++;

                            // show frame
                            try
                            {
                                imageOutput.Image = frame_buffer[(frame_buffer_pos) % frame_buffer_count].Bitmap;
                            }
                            catch (Exception)
                            {
                                // System.Windows.MessageBox.Show(ex.ToString(), "PAL/NTSC SWITCH");
                            }
                        }

                        // stop fps timer
                        watch.Stop();
                        if (watch.ElapsedMilliseconds > 0)
                        {
                            fps = (1000 / watch.ElapsedMilliseconds);
                        }
                    }
                    catch (Exception)
                    {
                    }
                }

            } while (stream_active && !_video_mode_change_restart);
            video_listener.Close();

            // Clear buffers
            video_listener.Dispose();
            video_listener = null;

            for (int i = 0; i < frame_buffer_count; i++)
            {
                frame_buffer[i].Dispose();
            }
            frame_buffer = null;

          //  _videowriter_frame_buffer.Dispose();
          //  _videowriter_frame_buffer = null;

            rawImage = null;

            GC.Collect();

            stream_active = false;
        }

        private void screenupdater_Tick(object sender, EventArgs e)
        {
            try
            {
                /*
                 // update ms/frame
                if (fps > 0)
                {
                    Text = "U64 Streamer";

                }
                else
                {
                    Text = "U64 Streamer";
                }*/

                if(frmdiag.Visible)
                {
                    if( fps>0 )
                    {
                        frmdiag.lbFPS.Text = fps.ToString() + " / " + (1000 / fps) + "ms/frame";
                    }
                    else
                    {
                        frmdiag.lbFPS.Text = "-";
                    }
                    frmdiag.lbVideoMode.Text = ((video_mode == 0) ? "PAL" : "NTSC") + " mode";
                    frmdiag.lbPacketLossVideo.Text = video_packet_loss.ToString();
                    frmdiag.lbReceivedVideoBytes.Text = video_received_bytes.ToString() + " (" + ((video_received_bytes / 1048576)).ToString() + "MB)";
                    frmdiag.lbReceivedAudioBytes.Text = audio_received_bytes.ToString() + " (" + ((audio_received_bytes / 1048576)).ToString() + "MB)";
                    frmdiag.lbReceivedVideoFrames.Text = video_frame_cnt.ToString();
                    frmdiag.lbReceivedAudioSamples.Text = audio_sample_cnt.ToString();
                }
            }
            catch
            {
                // go on..
            }
        }

        public void save_image()
        {
            if (video_stream_active)
            {
                // get image from picturebox1
                try
                {
                    // set image save format
                    ImageFormat format = ImageFormat.Png;
                    PictureBox output = new PictureBox();

                    Bitmap original = new Bitmap(imageOutput.Image);

                    // set buffers
                    DirectBitmap dbm768 = new DirectBitmap(imWidth * 2, imHeight * 2);
                    DirectBitmap dbm1536 = new DirectBitmap(imWidth * 4, imHeight * 4);
                    Color color = new Color();

                    // scale images
                    for (UInt16 y = 0; y < imHeight; y++)
                    {
                        for (UInt16 x = 0; x < imWidth; x++)
                        {
                            // get color from original
                            color = original.GetPixel(x, y);

                            // scale to 768
                            dbm768.SetPixel(x * 2, y * 2, color);
                            dbm768.SetPixel(x * 2 + 1, y * 2, color);

                            dbm768.SetPixel(x * 2, y * 2 + 1, color);
                            dbm768.SetPixel(x * 2 + 1, y * 2 + 1, color);

                            // scale to 1536
                            dbm1536.SetPixel(x * 4, y * 4, color);
                            dbm1536.SetPixel(x * 4 + 1, y * 4, color);
                            dbm1536.SetPixel(x * 4 + 2, y * 4, color);
                            dbm1536.SetPixel(x * 4 + 3, y * 4, color);

                            dbm1536.SetPixel(x * 4, y * 4 + 1, color);
                            dbm1536.SetPixel(x * 4 + 1, y * 4 + 1, color);
                            dbm1536.SetPixel(x * 4 + 2, y * 4 + 1, color);
                            dbm1536.SetPixel(x * 4 + 3, y * 4 + 1, color);

                            dbm1536.SetPixel(x * 4, y * 4 + 2, color);
                            dbm1536.SetPixel(x * 4 + 1, y * 4 + 2, color);
                            dbm1536.SetPixel(x * 4 + 2, y * 4 + 2, color);
                            dbm1536.SetPixel(x * 4 + 3, y * 4 + 2, color);

                            dbm1536.SetPixel(x * 4, y * 4 + 3, color);
                            dbm1536.SetPixel(x * 4 + 1, y * 4 + 3, color);
                            dbm1536.SetPixel(x * 4 + 2, y * 4 + 3, color);
                            dbm1536.SetPixel(x * 4 + 3, y * 4 + 3, color);
                        }
                    }

                    string img_file_suffix = "";

                    if (frmsSettings.image_size == 0)
                    {
                        // Save image 384
                        output.Image = original;
                        img_file_suffix = "-0-384";
                    }
                    else if (frmsSettings.image_size == 1)
                    {
                        // Save image 768
                        output.Image = dbm768.Bitmap;
                        img_file_suffix = "-1-768";
                    }
                    else if (frmsSettings.image_size == 2)
                    {
                        // Save image 1536
                        output.Image = dbm1536.Bitmap;
                        img_file_suffix = "-2-1536";
                    }

                    if (!image_ctrl_c)
                    {
                        

                        /*
                         * show capture options Dialog
                         * 
                         * Diaglogresults:
                         * ===============
                         * Cancel    = Cancel (2)
                         * File      = Retry (4)
                         * Clipboard = Ignore (5)
                         * Both      = Yes (6)
                         * 
                         */
                        DialogResult save_dialog_result = frmDialogSaveImage.ShowDialog(this);

                        // Save to file ( button File or Both )
                        if (save_dialog_result == DialogResult.Retry || save_dialog_result == DialogResult.Yes)
                        {
                            output.Image.Save("capture/U64Stream-" + DateTime.Now.ToString("yyyyMMddHHmmss") + img_file_suffix + ".png", format);
                        }
                        // Save to clipboard ( button Clipboard or Both )
                        if (save_dialog_result == DialogResult.Ignore || save_dialog_result == DialogResult.Yes)
                        {
                            Clipboard.SetImage(output.Image);
                        }
                    }
                    else {

                        // CTRL+C is pressed
                        Clipboard.SetImage(output.Image);
                        image_ctrl_c = false;
                    }

                    // unset objects
                    dbm768.Dispose();
                    dbm1536.Dispose();
                    original.Dispose();
                    output.Dispose();
                }
                catch
                {
                    // proceed
                }
            }
            else
            {
                MessageBox.Show("No active stream..");
            }
        }

        private void btn_save_image_Click(object sender, EventArgs e)
        {
            save_image();
        }

        public void stream_control()
        {
            // start U64 streams
            if (!stream_active)
            {
                // Set screen mode
                set_screen_mode();

                // start streams remotely
                start_u64_streams();

                // set stream active
                stream_active = true;

                // start streams
                Console.WriteLine("init");
                if (!video_stream_worker.IsBusy) video_stream_worker.RunWorkerAsync();
                if (!audio_stream_worker.IsBusy) audio_stream_worker.RunWorkerAsync();

                // update menu
                frmmenu.btn_stream_control.Text = "Stop stream";
                frmmenu.btn_stream_control.ImageIndex = 1;
            }
            else
            if (stream_active)
            {
                stream_active = false;

                // stop streams
                video_stream_worker.CancelAsync();
                audio_stream_worker.CancelAsync();

                // close listeners to stop receive buffer
                if (video_listener!=null) video_listener.Close();
                if (audio_listener!=null) audio_listener.Close();

                // update menu
                frmmenu.btn_stream_control.Text = "Start stream";
                frmmenu.btn_stream_control.ImageIndex = 0;
            }
        }

        private void btn_stream_control_Click(object sender, EventArgs e)
        {
            stream_control();
        }

        public void Color_Selector()
        {
        }

        public void settings()
        {
            if (stream_active)
            {
                DialogResult result1 = MessageBox.Show("Stop stream to edit settings ?", "Stream is active", MessageBoxButtons.YesNo);
                if (result1 == DialogResult.Yes)
                {
                    stream_control();

                }
            }

            if (!stream_active)
            {
                frmsSettings.Top = this.Top + (this.Height / 2) - (frmsSettings.Height / 2);
                frmsSettings.Left = this.Left + (this.Width / 2) - (frmsSettings.Width / 2);

                frmsSettings.ShowDialog();

                frame_buffer_count = frmsSettings.frame_buffer_count + 1;
                video_listen_port = frmsSettings.video_listen_port;
                audio_listen_port = frmsSettings.audio_listen_port;
                audio_output_device = frmsSettings.audio_output_device;

                // set active color table
                set_active_colors();

                stream_control();
            }
        }

        private void bt_settings_Click(object sender, EventArgs e)
        {
            settings();
        }

        private void send_keystroke( byte key )
        {
            // see: https://github.com/GideonZ/1541ultimate/blob/master/software/network/socket_dma.cc
            // start remote..
            try
            {
                // Create tcpclient
                TcpClient client = new TcpClient(frmsSettings.u64_ip_address, 64);
                // Create client stream
                NetworkStream stream = client.GetStream();

                // Create & send message to start video stream
                Byte[] msg_start_video_stream = { 0x03, 0xff, 0x1,0x0, key }; // 0xff03 = SOCKET_CMD_KEYB, 0x0001=length, key=key
                stream.Write(msg_start_video_stream, 0, msg_start_video_stream.Length);

                // Close everything.
                stream.Close();
                client.Close();
            }
            catch (ArgumentNullException ex)
            {
                MessageBox.Show("ArgumentNullException: " + ex.Message);
            }
            catch (SocketException ex)
            {
                MessageBox.Show("SocketException: " + ex.Message);
            }
        }

        public void u64_run_prg( string f_name, bool mount_run = true)
        {
            if (!frmsSettings.remotestart)
            {
                MessageBox.Show("To use filedialog or drag and drop,\n the 'remote start' must be enabled (see settings).");
                return;
            }

            // Save last file path
            frmsSettings.last_file_path = Path.GetDirectoryName(f_name);
            frmsSettings.save_settings();

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
                if(mount_run)
                {
                    msg_run_header = new byte[] { 0x0b, 0xff, (byte)(fsize & 0xFF), (byte)((fsize & 0xFF00) >> 8), (byte)((fsize & 0xFF0000) >> 16) };
                }
                else
                {
                    msg_run_header = new byte[] { 0x0a, 0xff, (byte)(fsize & 0xFF), (byte)((fsize & 0xFF00) >> 8), (byte)((fsize & 0xFF0000) >> 16) };
                }
            }
            else
            {
                MessageBox.Show("File not supported.\nOnly PRG & D64 files are supported.");

                return;
            }


            // Create tcpclient
            TcpClient client = new TcpClient(frmsSettings.u64_ip_address, 64);
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

        private void ResetBackgroundWorker()
        {
            video_stream_worker.CancelAsync();

            Task taskStart = Task.Run(() =>
            {
                Thread.Sleep(1000);
                video_stream_worker.RunWorkerAsync();
            });
        }

        private void start_u64_streams()
        {
            // exit if remote start is disabled
            if (!frmsSettings.remotestart) return;

            // test if ip address is reachable
            Ping pinger = null;
            try
            {
                pinger = new Ping();
                PingReply reply = pinger.Send(frmsSettings.u64_ip_address, frmsSettings.pingtimeout);

                if (reply.Status != IPStatus.Success)
                {
                    MessageBox.Show("Can't reach the configured IP/FQDN address of the Ultimate 64!\n\nCheck if the Ultimate 64 is online\nor change it to a valid IP address\nor disable remote start in settings window.");

                    return;
                }
            }
            catch (PingException)
            {
                MessageBox.Show("Can't reach the configured IP/FQDN address of the Ultimate 64!\n\nCheck if the Ultimate 64 is online\nor change it to a valid IP/FQDN address\nor disable remote start in settings window.");
                return;
            }
            finally
            {
                if (pinger != null)
                {
                    pinger.Dispose();
                }
            }

            // start remote..
            try
            {
                // Create tcpclient
                TcpClient client = new TcpClient(frmsSettings.u64_ip_address, 64);

                // Create client stream
                NetworkStream stream = client.GetStream();

                // Create & send message to start video stream
                Byte[] msg_start_video_stream = { 0x20, 0xff, 0x00, 0x00 };
                stream.Write(msg_start_video_stream, 0, msg_start_video_stream.Length);

                // Create & send message to start audio stream
                Byte[] msg_start_audio_stream = { 0x21, 0xff, 0x00, 0x00 };
                stream.Write(msg_start_audio_stream, 0, msg_start_audio_stream.Length);

                // Close everything.
                stream.Close();
                client.Close();
            }
            catch (ArgumentNullException ex)
            {
                MessageBox.Show("ArgumentNullException: " + ex.Message);
            }
            catch (SocketException ex)
            {
                MessageBox.Show("SocketException: " + ex.Message);
            }
        }

        public void audio_stream_recorder_toggle()
        {
            if (!stream_active)
            {
                MessageBox.Show("No stream active..");
                return;
            }

            if (!audio_stream_rec_active)
            {
                waveFormat = new WaveFormat(SampleRate, 16, 2);
                audio_writer = new WaveFileWriter("capture/U64Stream-audio-" + DateTime.Now.ToString("yyyyMMddHHmmss") + ".wav", waveFormat);
                audio_stream_rec_active = true;
                frmmenu.btn_audio_record.ImageIndex = 8;
            }
            else
            {
                audio_stream_rec_active = false;
                audio_writer.Close();
                audio_writer.Dispose();

                frmmenu.btn_audio_record.ImageIndex = 7;
            }
        }

        private void audio_stream_worker_DoWork(object sender, System.ComponentModel.DoWorkEventArgs e)
        {
            // set stream active


            audio_stream_active = true;

            // setup udp listener
            audio_listener = new UdpClient(audio_listen_port);
            IPEndPoint groupEP = new IPEndPoint(IPAddress.Any, audio_listen_port);

            // setup buffers
            // Set buffer / decoder
            BufferedWaveProvider OutputBuffer = new BufferedWaveProvider(new WaveFormat(SampleRate, 16, 2));
            OutputBuffer.DiscardOnBufferOverflow = false;


            // show ASIO devices
            /*
            string[] devices = AsioOut.GetDriverNames();
            foreach(string device in devices)
            {
                Console.WriteLine(device);
            }
            */
            if (audio_output_device==0)
            {
                waveOut = new WaveOut();
                waveOut.Init(OutputBuffer);
                waveOut.Play();
            }
            else if (audio_output_device == 1)
            {
                directSoundOut = new DirectSoundOut();
                directSoundOut.Init(OutputBuffer);
                directSoundOut.Play();
            }
            else if (audio_output_device == 2)
            {
                wasapiOut = new WasapiOut();
                wasapiOut.Init(OutputBuffer);
                wasapiOut.Play();
            }

            // start stream processing
            try
            {
                do
                {
                    audio_receive_byte_array = audio_listener.Receive(ref groupEP);
                    if (audio_receive_byte_array.Length == 770)
                    {
                        audio_received_bytes += audio_receive_byte_array.Length;
                        OutputBuffer.AddSamples(audio_receive_byte_array, 2, 768);

                        audio_sample_cnt += 192;
                        
                        if (audio_stream_rec_active)
                        {
                            Array.Copy(audio_receive_byte_array, 2, audio_writer_buffer, 0, 768);
                            audio_writer.Write(audio_writer_buffer, 0, audio_writer_buffer.Length);
                        }
                    }
                } while (audio_stream_active);
            }
            catch
            {
                
                // please proceed..
            }

            if (audio_output_device == 0)
            {
                waveOut.Dispose();
                waveOut.Stop();
            }
            else if (audio_output_device == 1)
            {
                directSoundOut.Dispose();
                directSoundOut.Stop();
            }
            else if (audio_output_device == 2)
            {
                wasapiOut.Dispose();
                wasapiOut.Stop();
            }

            audio_listener.Close();
        }

        private void resizeWindow()
        {
            // resize window..
            try
            {
                if(ActiveForm!=null)
                {
                    ActiveForm.Height = (short)(imageOutput.Width / imRatio) + frm_border_size.Height;// + menuHeight;

                    frmmenu.Width = ActiveForm.Width;
                    frmmenu.Location = new Point(ActiveForm.Left, ActiveForm.Bottom); // or any value to set the location
                }
            }
            catch( Exception ex )
            {
                MessageBox.Show(ex.Message);
            }
        }

        private void imageOutput_SizeChanged(object sender, EventArgs e)
        {
            // Calc image scale ratio
            imRatio = (decimal)imWidth / (decimal)imHeight;
            imageOutput.Height = (short)(imageOutput.Width / imRatio);
        }
        

        [System.Security.Permissions.PermissionSet(System.Security.Permissions.SecurityAction.Demand, Name = "FullTrust")]
        protected override void WndProc(ref Message m)
        {
            // Listen for operating system messages.
            switch (m.Msg)
            {
                // WM_SIZING
                case 0x0214:

                    // which handle is used ?
                    /*
                    switch(m.WParam.ToInt32())
                    {
                        // WMSZ_RIGHT
                        case 2: WM_SIZING_msg = 2; break;
                        // WMSZ_BOTTOM
                        case 6 : WM_SIZING_msg = 6; break;
                        // WMSZ_BOTTOMRIGHT
                        case 8: WM_SIZING_msg = 8; break;
                    }
                    */
                    break;
            }
            base.WndProc(ref m);
        }

        private void button1_Click(object sender, EventArgs e)
        {
            frm_about.ActiveForm.Show();
        }

        private void bt_about_Click(object sender, EventArgs e)
        {
            frmabout.ShowDialog();
        }

        private void video_stream_worker_RunWorkerCompleted(object sender, System.ComponentModel.RunWorkerCompletedEventArgs e)
        {
            video_stream_active = false;
            imageOutput.Image = logo;
            fps = 0;

            // video mode changed preform restart
            if (_video_mode_change_restart)
            {
                _video_mode_change_restart = false;
                stream_control();
            }
        }

        private void audio_stream_worker_RunWorkerCompleted(object sender, System.ComponentModel.RunWorkerCompletedEventArgs e)
        {
            audio_stream_active = false;
        }

        private void frm_main_Move(object sender, EventArgs e)
        {
            if( ActiveForm!=null)
            {

                frmmenu.Width = ActiveForm.Width;
                frmmenu.Location = new Point(ActiveForm.Left, ActiveForm.Bottom); // or any value to set the location
            }
            
        }

        public void reset_u64()
        {
            // Create tcpclient
            TcpClient client = new TcpClient(frmsSettings.u64_ip_address, 64);

            // Create client stream
            NetworkStream stream = client.GetStream();

            // Create & send SOCKET_CMD_RESET
            Byte[] _MSG_SOCKET_CMD_RESET = { 0x04, 0xff,0x00,0x00 };
            stream.Write(_MSG_SOCKET_CMD_RESET, 0, _MSG_SOCKET_CMD_RESET.Length);

            // Close everything.
            stream.Close();
            client.Close();
        }

        private byte key_remapping(byte key_in, bool shift )
        {
            byte key_out = 0;

            // remap shifted keys
            if ( shift)
            {
                switch (key_in)
                {
                    case 9: key_out = 3; break; // stop (run)

                    case 49: key_out = 33; break; // !
                    case 50: key_out = 64; break; // @
                    case 51: key_out = 35; break; // #
                    case 52: key_out = 36; break; // $
                    case 53: key_out = 37; break; // %
                    case 54: key_out = 64; break; // ^
                    case 55: key_out = 38; break; // &
                    case 56: key_out = 42; break; // *
                    case 57: key_out = 40; break; // 
                    case 48: key_out = 41; break; // 0
                    case 186: key_out = 58; break; // :
                    case 188: key_out = 60; break; // <
                    case 189: key_out = 45; break; // -
                    case 187: key_out = 43; break; // +
                    case 190: key_out = 62; break; // >
                    case 191: key_out = 63; break; // ?
                    case 222: key_out = 34; break; // "
                }
            }
            else
            {
                switch (key_in)
                {
                    // backspace / delete
                    case 46: 
                    case 8: key_out = 20; break;

                    case 27: key_out = 0x3; break; // stop (esc)
                    case 9: key_out = 131; break; // run (tab)

                    case 36: key_out = 19; break; // home
                    case 45: key_out = 148; break; // insert

                    case 37: key_out = 157; break; // cursor left
                    case 38: key_out = 145; break; // cursor up
                    case 39: key_out = 29; break; // cursor right
                    case 40: key_out = 17; break; // cursor down
                    
                    case 112: key_out = 133; break; // F1
                    case 113: key_out = 134; break; // F2
                    case 114: key_out = 135; break; // F3
                    case 115: key_out = 136; break; // F4
                    case 116: key_out = 137; break; // F5
                    case 117: key_out = 138; break; // F6
                    case 118: key_out = 139; break; // F7
                    case 119: key_out = 140; break; // F8
                    case 186: key_out = 59; break; // ;
                    case 188: key_out = 44; break; // ,
                    case 187: key_out = 61; break; // =
                    case 189: key_out = 45; break; // -
                    case 190: key_out = 46; break; // .
                    case 191: key_out = 47; break; // /
                    case 219: key_out = 91; break; // [
                    case 221: key_out = 93; break; // ]
                    case 222: key_out = 39; break; // '
                    default: key_out = key_in; break;
                }
            }

            return key_out;
        }

        private void frm_main_KeyDown(object sender, KeyEventArgs e)
        {
            if((byte)e.KeyCode!=16)
            {
                // Console.WriteLine(e.KeyCode + "=" + e.KeyValue + " / " + e.KeyData);
                send_keystroke(key_remapping((byte)e.KeyCode, e.Shift));
            }
            if(e.Control && e.KeyValue== 67) 
            {
                image_ctrl_c = true;
                save_image();
            }
        }

        private void frm_main_DragDrop(object sender, DragEventArgs e)
        {
        }

        private void frm_main_Load(object sender, EventArgs e)
        {
            imageOutput.AllowDrop = true;
        }

        private void imageOutput_DragDrop(object sender, DragEventArgs e)
        {
            if( !frmsSettings.remotestart )
            {
                MessageBox.Show("To use drag and drop the 'remote start' must be enabled in the settings.");
                return;
            }

            var data = e.Data.GetData(DataFormats.FileDrop);

            if (data != null)
            {
                bool mount_run = ((e.KeyState & 0x8) == 0x8) ? false : true;

                var f_names = data as string[];

                u64_run_prg(f_names[0], mount_run);
            }
        }

        private void imageOutput_DragEnter(object sender, DragEventArgs e)
        {
            e.Effect = DragDropEffects.Copy;
        }

        private void video_writer_worker_DoWork(object sender, System.ComponentModel.DoWorkEventArgs e)
        {

        }

        /* public void video_writer_worker_DoWork(object sender, System.ComponentModel.DoWorkEventArgs e)
             {
                 VideoFileWriter _writer;

                 _writer = new VideoFileWriter();

                 _writer.VideoCodec = VideoCodec.Mpeg4;
                 _writer.Width = imWidth;
                 _writer.Height = imHeight;
                 _writer.FrameRate = 50;
                 _writer.BitRate = 2000000;

                 _writer.AudioCodec = AudioCodec.Mp3;
                 _writer.AudioBitRate = 48000;
                 _writer.SampleRate = 768;
                 _writer.SampleFormat = AVSampleFormat.Format16bitSigned;
                 _writer.AudioLayout = AudioLayout.Stereo;

                 _writer.Open("test.avi");

                 do
                 {
                     if( _videowriter_frame_available )
                     {
                         _videowriter_frame_available = false;
                         try
                         {

                             _writer.WriteVideoFrame(_videowriter_frame_buffer);
                         }
                         catch( Exception ex )
                         {
                             Console.WriteLine(ex.Message);
                         }



                     }
                 } while (stream_active);

                 _writer.Close();
                 _writer.Dispose();
             }
             */
    }
    
}
