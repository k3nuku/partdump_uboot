using System;
using System.Collections.Generic;
using System.Linq;
using System.IO.Ports;
using System.IO;

namespace partdump_uboot
{
    using System;
    using System.Text;
    using System.Threading;

    /// <summary>
    /// An ASCII progress bar
    /// </summary>
    public class ProgressBar : IDisposable, IProgress<double>
    {
        private const int blockCount = 10;
        private readonly TimeSpan animationInterval = TimeSpan.FromSeconds(1.0 / 8);
        private const string animation = @"|/-\";

        private readonly Timer timer;

        private double currentProgress = 0;
        private string currentText = string.Empty;
        private bool disposed = false;
        private int animationIndex = 0;

        public ProgressBar()
        {
            timer = new Timer(TimerHandler);

            // A progress bar is only for temporary display in a console window.
            // If the console output is redirected to a file, draw nothing.
            // Otherwise, we'll end up with a lot of garbage in the target file.
            if (!Console.IsOutputRedirected)
            {
                ResetTimer();
            }
        }

        public void Report(double value)
        {
            // Make sure value is in [0..1] range
            value = Math.Max(0, Math.Min(1, value));
            Interlocked.Exchange(ref currentProgress, value);
        }

        private void TimerHandler(object state)
        {
            lock (timer)
            {
                if (disposed) return;

                int progressBlockCount = (int)(currentProgress * blockCount);
                int percent = (int)(currentProgress * 100);
                string text = string.Format("[{0}{1}] {2,3}% {3}",
                    new string('#', progressBlockCount), new string('-', blockCount - progressBlockCount),
                    percent,
                    animation[animationIndex++ % animation.Length]);
                UpdateText(text);

                ResetTimer();
            }
        }

        private void UpdateText(string text)
        {
            // Get length of common portion
            int commonPrefixLength = 0;
            int commonLength = Math.Min(currentText.Length, text.Length);
            while (commonPrefixLength < commonLength && text[commonPrefixLength] == currentText[commonPrefixLength])
            {
                commonPrefixLength++;
            }

            // Backtrack to the first differing character
            StringBuilder outputBuilder = new StringBuilder();
            outputBuilder.Append('\b', currentText.Length - commonPrefixLength);

            // Output new suffix
            outputBuilder.Append(text.Substring(commonPrefixLength));

            // If the new text is shorter than the old one: delete overlapping characters
            int overlapCount = currentText.Length - text.Length;
            if (overlapCount > 0)
            {
                outputBuilder.Append(' ', overlapCount);
                outputBuilder.Append('\b', overlapCount);
            }

            Console.Write(outputBuilder);
            currentText = text;
        }

        private void ResetTimer()
        {
            timer.Change(animationInterval, TimeSpan.FromMilliseconds(-1));
        }

        public void Dispose()
        {
            lock (timer)
            {
                disposed = true;
                UpdateText(string.Empty);
            }
        }
    }

    class Program
    {
        static string _dumpBuffer = "";

        static ManualResetEvent rwSync = new ManualResetEvent(false);
        static SerialPort _serialPort;
        
        static void Main(string[] args) // args 0: portname, 1: baudrate, 2: outputtype, 3: outputfolder
        {
            if (args.Length < 4)
                Console.WriteLine("partdump_uboot\nUsage: partdump_uboot [port] [baud] [outputtype] [outputfolder]");

            Console.WriteLine("partdump_uboot\n------------\nPlease connect power source to the target device.\n");
            
            // Create a new SerialPort object with default settings.
            using (_serialPort = new SerialPort())
            {
                // Allow the user to set the appropriate properties.
                _serialPort.PortName = args[0];
                _serialPort.BaudRate = Int32.Parse(args[1]);
                _serialPort.Parity = Parity.None;
                _serialPort.DataBits = 8;
                _serialPort.StopBits = StopBits.One;
                _serialPort.Handshake = Handshake.XOnXOff;

                // Set the read/write timeouts
                _serialPort.ReadTimeout = 500;
                _serialPort.WriteTimeout = 500;

                _serialPort.Open();
                _serialPort.DiscardInBuffer();
                _serialPort.DiscardOutBuffer();

                new Thread(new ThreadStart(Read)).Start();

                Console.WriteLine("Booting up u-boot environment... ");

                Console.Write("Entering u-boot Command line interface... ");
                //EnterUBootCLI();
                Console.WriteLine("Done.");

                //Console.Write("Dumping disk image at 0x0 ~ 0x1... ");
                //DumpPartition(0, 10, _serialPort);
                //Console.WriteLine("Done, {0} lines", _dumpBuffer.Count);

                Console.Write("Dumping disk image via uboot serial output... ");

                using (var pgbar = new ProgressBar())
                {
                    var blkReadSize = 1;
                    var filename = string.Format("output_{0}_whole.bin",
                                                /*DateTime.Now.ToString("yyyyMMdd_hhmmss")*/
                                                "20191007_013340");

                    for (int k = 7634944; k < 16384 * 512; k += blkReadSize)
                    {
                        bool _success = false;

                        while (!_success)
                        {
                            try
                            {
                                // mmc dump 0 3 -> 4 7 -> 8 11
                                DumpPartition(k, k + blkReadSize - 1, _serialPort);

                                var _bufRefinedStage1 = _dumpBuffer.Replace("\r", "").Split('\n')
                                                                                     .Where(e =>
                                                                                           !e.Trim().StartsWith("ESPRESSO")
                                                                                        && !e.Trim().StartsWith("emmc")
                                                                                        && !e.Trim().StartsWith("/*")
                                                                                        && !e.Trim().StartsWith("mmc")
                                                                                        && !e.Trim().Equals(""));

                                var _dumpByteBuffer = new List<byte>();

                                foreach (var str in _bufRefinedStage1)
                                {
                                    var _line = str.Split(' ');

                                    for (int i = 0; i < 16; i++)
                                        _dumpByteBuffer.AddRange(StringToByteArray(_line[i]));
                                }

                                using (var sw = new FileStream(filename, FileMode.Append))
                                {
                                    sw.Write(_dumpByteBuffer.ToArray(), 0, _dumpByteBuffer.Count);
                                }

                                _dumpBuffer = "";

                                pgbar.Report((double)k / (16384 * 512));

                                _success = true;
                            }
                            catch
                            {

                            }
                        }
                    }
                }
                

                Console.WriteLine("Done.");
                   
                new ManualResetEvent(false).WaitOne();
            }
        }

        /*
           DEC	OCT	HEX	BIN	Symbol	HTML Number     Description
            48	060	30	00110000	0	&#48;	 	Zero
            49	061	31	00110001	1	&#49;	 	One
            50	062	32	00110010	2	&#50;	 	Two
            51	063	33	00110011	3	&#51;	 	Three
            52	064	34	00110100	4	&#52;	 	Four
            53	065	35	00110101	5	&#53;	 	Five
            54	066	36	00110110	6	&#54;	 	Six
            55	067	37	00110111	7	&#55;	 	Seven
            56	070	38	00111000	8	&#56;	 	Eight
            57	071	39	00111001	9	&#57;	 	Nine
            97	141	61	01100001	a	&#97;	 	Lowercase a
            98	142	62	01100010	b	&#98;	 	Lowercase b
            99	143	63	01100011	c	&#99;	 	Lowercase c
            100	144	64	01100100	d	&#100;	 	Lowercase d
            101	145	65	01100101	e	&#101;	 	Lowercase e
            102	146	66	01100110	f	&#102;	 	Lowercase f
         */
        private static byte[] StringToByteArray(string hex)
            => Enumerable.Range(0, hex.Length)
                         .Where(x => x % 2 == 0)
                         .Select(x => Convert.ToByte(hex.Substring(x, 2), 16))
                         .ToArray();
        
        private static void Read()
        {
            while (true)
            {
                if (!_serialPort.IsOpen)
                    break;

                var _read = _serialPort.BytesToRead;
                byte[] _byteArr;

                if (_read > 0)
                {
                    _byteArr = new byte[_read];
                    _serialPort.Read(_byteArr, 0, _read);
                    _dumpBuffer += Encoding.ASCII.GetString(_byteArr);

                    if (_dumpBuffer.Contains("ESPRESSO7570 #"))
                        OutputComplete();
                }
            }
        }

        private static void OutputComplete()
        {
            rwSync.Set();
        }

        public static void EnterUBootCLI(SerialPort _serialPort)
        {
            for (int i = 0; i < 2; i++)
            {
                _serialPort.Write(new byte[1] { 0x0D }, 0, 1);
                Thread.Sleep(100);
            }
        }

        public static void DumpPartition(int startBlock, int endBlock, SerialPort _serialPort)
        {
            rwSync.Reset();
            _serialPort.DiscardInBuffer();
            _serialPort.DiscardOutBuffer();

            var _cmd = string.Format("mmc dump {0} {1}",
                startBlock.ToString("X"), endBlock.ToString("X"));

            _serialPort.WriteLine(_cmd);
            rwSync.WaitOne();
        }
    }
}
