using foggycam.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ProtoBuf;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using WebSocket4Net;

namespace foggycam
{
    class Program
    {
        static string ISSUE_TOKEN = "";
        static string COOKIE = "";
        static string API_KEY = "";
        static string USER_AGENT = "";
        static string NEST_API_HOSTNAME = "";
        static string CAMERA_API_HOSTNAME = "";
        static string CAMERA_AUTH_COOKIE = "";

        static WebSocket ws;

        static int videoChannelId = -1;
        static int audioChannelId = -1;

        static string NEXUS_HOST = "";
        static string CAMERA_UUID = "";
        static string HOMEBOX_CAMERA_ID = "";
        static string TOKEN = "";

        static dynamic CAMERA = null;
        static dynamic CONFIG = null;

        static List<byte[]> videoStream = new List<byte[]>();
        static List<byte[]> audioStream = new List<byte[]>();

        static AutoResetEvent autoEvent = new AutoResetEvent(false);
        static Random random = new Random();

        static async Task Main(string[] args)
        {
            Console.WriteLine("[log] Reading config...");
            try
            {
                CONFIG = JsonConvert.DeserializeObject(File.ReadAllText("camera_config.json"));
                ISSUE_TOKEN = CONFIG.issue_token;
                COOKIE = CONFIG.cookie;
                API_KEY = CONFIG.api_key;
                USER_AGENT = CONFIG.user_agent;
                NEST_API_HOSTNAME = CONFIG.nest_api_hostname;
                CAMERA_API_HOSTNAME = CONFIG.camera_api_hostname;
                CAMERA_AUTH_COOKIE = CONFIG.camera_auth_cookie;

                Console.WriteLine("[log] Config loaded.");
            }
            catch
            {
                Console.WriteLine("[error] Could not read config.");
                Environment.Exit(1);
            }

            TOKEN = await GetGoogleToken(ISSUE_TOKEN, COOKIE);

            if (!string.IsNullOrEmpty(TOKEN))
            {
                Console.WriteLine($"[log] Token succesfully obtained.");

                var data = await GetCameras(TOKEN);
                CAMERA = (dynamic)data;
                NEXUS_HOST = (string)CAMERA.items[0].direct_nexustalk_host;
                CAMERA_UUID = (string)CAMERA.items[0].uuid;

                ThreadPool.QueueUserWorkItem(new WaitCallback(StartWork), autoEvent);
                autoEvent.WaitOne();
            }
            else
            {
                Console.WriteLine("[error] Could not get the token.");
            }
        }

        private async static void StartWork(object state)
        {
            SetupConnection(NEXUS_HOST + ":80/nexustalk", CAMERA_UUID, HOMEBOX_CAMERA_ID, TOKEN);

            while (true)
            {
                await Task.Delay(15000);
                var pingBuffer = PreformatData(PacketType.PING, new byte[0]);
                ws.Send(pingBuffer, 0, pingBuffer.Length);
            }
        }

        private static void StartPlayback(dynamic cameraInfo)
        {
            var primaryProfile = StreamProfile.VIDEO_H264_2MBIT_L40;

            string[] capabilities = ((JArray)cameraInfo.capabilities).ToObject<string[]>();
            var matchingCapabilities = from c in capabilities where c.StartsWith("streaming.cameraprofile") select c;

            List<int> otherProfiles = new List<int>();
            foreach (var capability in matchingCapabilities)
            {
                var cleanCapability = capability.Replace("streaming.cameraprofile.", "");
                var successParsingEnum = Enum.TryParse(cleanCapability, out StreamProfile targetProfile);

                if (successParsingEnum)
                {
                    otherProfiles.Add((int)targetProfile);
                }
            }

            if ((bool)cameraInfo.properties["audio.enabled"])
            {
                otherProfiles.Add((int)StreamProfile.AUDIO_AAC);
            }

            StartPlayback sp = new StartPlayback();
            sp.SessionId = random.Next(0, 100);
            sp.Profile = (int)primaryProfile;
            sp.OtherProfiles = otherProfiles.ToArray<int>();

            using (MemoryStream spStream = new MemoryStream())
            {
                Serializer.Serialize(spStream, sp);
                var formattedSPOutput = PreformatData(PacketType.START_PLAYBACK, spStream.ToArray());
                ws.Send(formattedSPOutput, 0, formattedSPOutput.Length);
            }
        }

        private async static void ProcessBuffers(List<byte[]> videoStream, List<byte[]> audioStream)
        {
            var videoBufferTask = GetFlatBytes(videoStream, true);
            var audioBufferTask = GetFlatBytes(audioStream, true);

            byte[][] data = await Task.WhenAll(videoBufferTask, audioBufferTask);

            var fileName = DateTime.Now.ToString("yyyy-dd-M--HH-mm-ss") + ".mp4";
            DumpToFile(data[0], data[1], fileName);
        }

        static async Task<byte[]> GetFlatBytes(List<byte[]> container, bool clear = false)
        {
            await Task.Delay(0);
            var buffer = container.SelectMany(a => a).ToArray();
            if (clear)
            {
                container.Clear();
            }
            return buffer;
        }

        static void DumpToFile(byte[] videoBuffer, byte[] audioBuffer, string fileName)
        {
            // Compile the initial video file (without any audio).
            var startInfo = new ProcessStartInfo(CONFIG.ffmpeg_path.ToString());
            startInfo.RedirectStandardInput = true;
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            startInfo.UseShellExecute = false;

            var argumentBuilder = new List<string>();
            argumentBuilder.Add("-loglevel panic");
            argumentBuilder.Add("-f h264");
            argumentBuilder.Add("-i pipe:");
            argumentBuilder.Add("-vsync vfr");
            argumentBuilder.Add("-c:v libx264");
            argumentBuilder.Add("-bf 0");
            argumentBuilder.Add("-pix_fmt yuv420p");
            argumentBuilder.Add("-an");
            argumentBuilder.Add(fileName);

            startInfo.Arguments = string.Join(" ", argumentBuilder.ToArray());

            var _ffMpegProcess = new Process();
            _ffMpegProcess.EnableRaisingEvents = true;
            _ffMpegProcess.OutputDataReceived += (s, e) => { Debug.WriteLine(e.Data); };
            _ffMpegProcess.ErrorDataReceived += (s, e) => { Debug.WriteLine(e.Data); };
            _ffMpegProcess.StartInfo = startInfo;

            Console.WriteLine($"\n[log] Starting write to {fileName}...");

            try
            {
                _ffMpegProcess.Start();
                _ffMpegProcess.BeginOutputReadLine();
                _ffMpegProcess.BeginErrorReadLine();

                Console.WriteLine("[log] Full video buffer: " + videoBuffer.Length);
                Console.WriteLine("[log] Full audio buffer: " + audioBuffer.Length);
                
                using (var memoryStream = new MemoryStream(videoBuffer))
                {
                    memoryStream.CopyTo(_ffMpegProcess.StandardInput.BaseStream);
                }

                _ffMpegProcess.StandardInput.BaseStream.Close();

                Process[] pname = Process.GetProcessesByName("ffmpeg");
                while (pname.Length > 0)
                {
                    pname = Process.GetProcessesByName("ffmpeg");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[error] An error occurred writing the video file.");
                Console.WriteLine($"[error] {ex.Message}");
            }

            argumentBuilder = new List<string>();
            argumentBuilder.Add($"-i {fileName}");
            argumentBuilder.Add("-i pipe:");
            argumentBuilder.Add($"foggycam_{fileName}");

            startInfo.Arguments = string.Join(" ", argumentBuilder.ToArray());

            var _ffMpegAudioProcess = new Process();
            _ffMpegAudioProcess.EnableRaisingEvents = true;
            _ffMpegAudioProcess.OutputDataReceived += (s, e) => { Debug.WriteLine(e.Data); };
            _ffMpegAudioProcess.ErrorDataReceived += (s, e) => { Debug.WriteLine(e.Data); };

            _ffMpegAudioProcess.StartInfo = startInfo;

            Console.WriteLine($"[log] Starting mux audio to {fileName}...");

            try
            {
                _ffMpegAudioProcess.Start();
                _ffMpegAudioProcess.BeginOutputReadLine();
                _ffMpegAudioProcess.BeginErrorReadLine();

                Console.WriteLine("[log] Got access to the process input stream.");
                using (var memoryStream = new MemoryStream(audioBuffer))
                {
                    memoryStream.CopyTo(_ffMpegAudioProcess.StandardInput.BaseStream);
                }
                Console.WriteLine("[log] Done writing input stream.");

                _ffMpegAudioProcess.StandardInput.BaseStream.Close();

                var pname = Process.GetProcessesByName("ffmpeg");
                while (pname.Length > 0)
                {
                    pname = Process.GetProcessesByName("ffmpeg");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[error] An error occurred writing the audio file.");
                Console.WriteLine($"[error] {ex.Message}");
            }

            Console.WriteLine("[log] Deleting auxiliary video file...");
            File.Delete(fileName);
            Console.WriteLine($"[log] Final file production completed.");
        }

        static void SetupConnection(string host, string cameraUuid, string deviceId, string token)
        {
            var tc = new TokenContainer();
            tc.OliveToken = token;

            using (var mStream = new MemoryStream())
            {
                Serializer.Serialize(mStream, tc);

                var helloRequestBuffer = new HelloContainer();
                helloRequestBuffer.ProtocolVersion = 3;
                helloRequestBuffer.Uuid = cameraUuid;
                helloRequestBuffer.DeviceId = deviceId;
                helloRequestBuffer.RequireConnectedCamera = false;
                helloRequestBuffer.UserAgent = USER_AGENT;
                helloRequestBuffer.ClientType = 3;
                helloRequestBuffer.AuthorizeRequest = mStream.GetBuffer();

                using (var finalMStream = new MemoryStream())
                {
                    Serializer.Serialize(finalMStream, helloRequestBuffer);

                    var dataBuffer = PreformatData(PacketType.HELLO, finalMStream.ToArray());
                    var target = $"wss://{host}";
                    Console.WriteLine($"[log] Setting up connection to {target}...");

                    ws = new WebSocket(target, sslProtocols: SslProtocols.Tls12 | SslProtocols.Tls11 | SslProtocols.Tls)
                    {
                        EnableAutoSendPing = true,
                        AutoSendPingInterval = 5
                    };
                    ws.Security.AllowNameMismatchCertificate = true;
                    ws.Security.AllowUnstrustedCertificate = true;
                    ws.DataReceived += Ws_DataReceived;
                    ws.Error += Ws_Error;

                    ws.Opened += (s, e) =>
                    {
                        ws.Send(dataBuffer, 0, dataBuffer.Length);
                    };
                    ws.Open();
                }
            }
        }

        static byte[] PreformatData(PacketType packetType, byte[] buffer)
        {
            byte[] finalBuffer;
            if (packetType == PacketType.LONG_PLAYBACK_PACKET)
            {
                var requestBuffer = new byte[5];
                requestBuffer[0] = (byte)packetType;
                var byteData = BitConverter.GetBytes((uint)buffer.Length);
                Array.Reverse(byteData);

                Buffer.BlockCopy(byteData, 0, requestBuffer, 1, byteData.Length);
                finalBuffer = new byte[requestBuffer.Length + buffer.Length];
                requestBuffer.CopyTo(finalBuffer, 0);
                buffer.CopyTo(finalBuffer, requestBuffer.Length);
            }
            else
            {
                var requestBuffer = new byte[3];
                requestBuffer[0] = (byte)packetType;
                var byteData = BitConverter.GetBytes((ushort)buffer.Length);
                Array.Reverse(byteData);

                Buffer.BlockCopy(byteData, 0, requestBuffer, 1, byteData.Length);
                finalBuffer = new byte[requestBuffer.Length + buffer.Length];
                requestBuffer.CopyTo(finalBuffer, 0);
                buffer.CopyTo(finalBuffer, requestBuffer.Length);
            }

            return finalBuffer;
        }

        public static byte[] FromHex(string hex)
        {
            hex = hex.Replace("-", "");
            byte[] raw = new byte[hex.Length / 2];
            for (int i = 0; i < raw.Length; i++)
            {
                raw[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            }
            return raw;
        }

        private static void Ws_DataReceived(object sender, WebSocket4Net.DataReceivedEventArgs e)
        {
            ProcessReceivedData(e.Data);
        }

        private static void ProcessReceivedData(byte[] buffer)
        {
            int type = buffer[0];
            try
            {
                int headerLength;
                uint length;
                if ((PacketType)type == PacketType.LONG_PLAYBACK_PACKET)
                {
                    headerLength = 5;
                    var lengthBytes = new byte[4];
                    Buffer.BlockCopy(buffer, 1, lengthBytes, 0, lengthBytes.Length);
                    Array.Reverse(lengthBytes);
                    length = BitConverter.ToUInt32(lengthBytes);
                }
                else
                {
                    headerLength = 3;
                    var lengthBytes = new byte[2];
                    Buffer.BlockCopy(buffer, 1, lengthBytes, 0, lengthBytes.Length);
                    Array.Reverse(lengthBytes);
                    length = BitConverter.ToUInt16(lengthBytes);
                }

                var payloadEndPosition = length + headerLength;

                Index top = headerLength;
                Index bottom = (Index)payloadEndPosition;

                var rawPayload = buffer[top..bottom];
                using (var dStream = new MemoryStream(rawPayload))
                {
                    HandlePacketData((PacketType)type, rawPayload);
                }

            }
            catch (Exception ex)
            {
                Console.WriteLine("[error] Error with packet capture.");
                Console.WriteLine(ex.Message);
            }

        }

        private static void HandlePacketData(PacketType type, byte[] rawPayload)
        {
            switch (type)
            {
                case PacketType.OK:
                    StartPlayback(CAMERA.items[0]);
                    break;
                case PacketType.PING:
                    Console.WriteLine("[log] Ping.");
                    break;
                case PacketType.PLAYBACK_BEGIN:
                    HandlePlaybackBegin(rawPayload);
                    break;
                case PacketType.PLAYBACK_PACKET:
                case PacketType.LONG_PLAYBACK_PACKET:
                    HandlePlayback(rawPayload);
                    break;
                case PacketType.REDIRECT:
                    HandleRedirect(rawPayload);
                    break;
                case PacketType.ERROR:
                    ws.Close();
                    HandleError(rawPayload);
                    break;
                default:
                    Console.WriteLine(type);
                    Console.WriteLine("[warn] Received unknown packet type.");
                    break;
            }
        }

        private static void HandleRedirect(byte[] rawPayload)
        {
            ws.Close();

            using (MemoryStream stream = new MemoryStream(rawPayload))
            {
                var packet = Serializer.Deserialize<Redirect>(stream);
                SetupConnection(packet.NewHost, CAMERA_UUID, HOMEBOX_CAMERA_ID, TOKEN);
            }
        }

        private static void HandleError(byte[] rawPayload)
        {
            using (MemoryStream stream = new MemoryStream(rawPayload))
            {
                var packet = Serializer.Deserialize<PlaybackError>(stream);

                Console.WriteLine($"[error] The capture errored out for the following reason: {packet.Reason}");
            }
        }

        private static void HandlePlayback(byte[] rawPayload)
        {
            using (MemoryStream stream = new MemoryStream(rawPayload))
            {
                var packet = Serializer.Deserialize<PlaybackPacket>(stream);

                if (packet.ChannelId == videoChannelId)
                {
                    byte[] h264Header = { 0x00, 0x00, 0x00, 0x01 };
                    var writingBlock = new byte[h264Header.Length + packet.Payload.Length];
                    h264Header.CopyTo(writingBlock, 0);
                    packet.Payload.CopyTo(writingBlock, h264Header.Length);

                    videoStream.Add(writingBlock);

                    Console.WriteLine($"[log] Video payload length: {writingBlock.Length, 5} Bitrate: {packet.TimestampDelta, 5} Video cache length: {videoStream.Count, 5}");
                }
                else if (packet.ChannelId == audioChannelId)
                {
                    audioStream.Add(packet.Payload);

                    Console.WriteLine($"[log] Audio payload length: {packet.Payload.Length, 5} Bitrate: {packet.TimestampDelta, 5} Audio cache length: {audioStream.Count,5}");
                }
                else
                {
                    Console.WriteLine("[log] Unknown channel: " + packet.Payload);
                }
            }

            // Once we reach a certain threshold, let's make sure that we flush the buffer.
            if (videoStream.Count == 1000)
            {
                ProcessBuffers(videoStream, audioStream);
            }
        }

        private static void HandlePlaybackBegin(byte[] rawPayload)
        {
            using (MemoryStream stream = new MemoryStream(rawPayload))
            {
                var packet = Serializer.Deserialize<PlaybackBegin>(stream);

                foreach (var registeredStream in packet.Channels)
                {
                    if ((CodecType)registeredStream.CodecType == CodecType.H264)
                    {
                        videoChannelId = registeredStream.ChannelId;
                    }
                    else if ((CodecType)registeredStream.CodecType == CodecType.AAC)
                    {
                        audioChannelId = registeredStream.ChannelId;
                    }
                }
            }
        }

        private static void Ws_Error(object sender, SuperSocket.ClientEngine.ErrorEventArgs e)
        {
            Console.WriteLine("[log] Socket errored out.");
            Console.WriteLine(e.Exception.Message);
            Console.WriteLine(e.Exception.InnerException);
            Console.WriteLine(e.Exception.GetType());

            SetupConnection(NEXUS_HOST + ":80/nexustalk", CAMERA_UUID, HOMEBOX_CAMERA_ID, TOKEN);
        }

        static async Task<object> GetCameras(string token)
        {
            var httpClient = new HttpClient();
            var request = new HttpRequestMessage
            {
                RequestUri = new Uri($"{CAMERA_API_HOSTNAME}/api/cameras.get_owned_and_member_of_with_properties"),
                Method = HttpMethod.Get,
                Headers =
                {
                    { "Cookie", $"user_token={token}" },
                    { "User-Agent", USER_AGENT },
                    { "Referer", NEST_API_HOSTNAME }
                }
            };

            var response = await httpClient.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                var rawResponse = await response.Content.ReadAsStringAsync();

                return JsonConvert.DeserializeObject(rawResponse);
            }

            return null;
        }

        static async Task<bool> SetCameraProperty(string cameraId, string token, string capability, string value)
        {
            var parameters = new Dictionary<string, string> { { $"{capability}", $"{value}" }, { "uuid", $"{cameraId}" } };
            var encodedContent = new FormUrlEncodedContent(parameters);

            var httpClient = new HttpClient();
            var request = new HttpRequestMessage
            {
                RequestUri = new Uri($"{CAMERA_API_HOSTNAME}/api/dropcams.set_properties"),
                Method = HttpMethod.Post,
                Content = encodedContent,
                Headers =
                {
                    { "Cookie", $"user_token={token}" },
                    { "User-Agent", USER_AGENT },
                    { "Referer", NEST_API_HOSTNAME }
                }
            };

            var response = await httpClient.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                var rawResponse = await response.Content.ReadAsStringAsync();

                var t = JsonConvert.DeserializeObject(rawResponse);
            }

            return false;
        }

        static async Task<string> GetGoogleToken(string issueToken, string cookie)
        {
            var tokenUri = new Uri(issueToken);
            string referrerDomain;
            try
            {
                referrerDomain = HttpUtility.ParseQueryString(tokenUri.Query).Get("ss_domain");
            }
            catch (Exception ex)
            {
                throw new ArgumentException("[error] Could not parse the referrer domain out of the token.");
            }

            try
            {
                var httpClient = new HttpClient();
                var request = new HttpRequestMessage
                {
                    RequestUri = new Uri(issueToken),
                    Method = HttpMethod.Get,
                    Headers =
                    {
                        { "Sec-Fetch-Mode", "cors" },
                        { "User-Agent", USER_AGENT },
                        { "X-Requested-With", "XmlHttpRequest" },
                        { "Referer", "https://accounts.google.com/o/oauth2/iframe" },
                        { "cookie", cookie }
                    }
                };

                var response = await httpClient.SendAsync(request);

                if (response.IsSuccessStatusCode)
                {
                    dynamic rawResponse = JsonConvert.DeserializeObject(await response.Content.ReadAsStringAsync());
                    var accessToken = rawResponse.access_token;

                    var parameters = new Dictionary<string, string> { { "embed_google_oauth_access_token", "true" }, { "expire_after", "3600s" }, { "google_oauth_access_token", $"{ accessToken}" }, { "policy_id", "authproxy-oauth-policy" } };
                    var encodedContent = new FormUrlEncodedContent(parameters);

                    request = new HttpRequestMessage
                    {
                        RequestUri = new Uri("https://nestauthproxyservice-pa.googleapis.com/v1/issue_jwt"),
                        Method = HttpMethod.Post,
                        Content = encodedContent,
                        Headers =
                        {
                            { "Authorization", $"Bearer {accessToken}" },
                            { "User-Agent", USER_AGENT },
                            { "x-goog-api-key", API_KEY },
                            { "Referer", referrerDomain }
                        }
                    };

                    response = await httpClient.SendAsync(request);
                    if (response.IsSuccessStatusCode)
                    {
                        rawResponse = JsonConvert.DeserializeObject(await response.Content.ReadAsStringAsync());
                        return rawResponse.jwt;
                    }
                    else
                    {
                        Console.WriteLine(response.StatusCode);
                    }
                }
            }
            catch (Exception ex)
            {
                throw new ApplicationException($"Could not perform Google authentication. {ex.Message}");
            }

            return null;
        }
    }
}
