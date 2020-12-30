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
        static bool authorized = false;

        static int videoChannelId = -1;
        static int audioChannelId = -1;

        static List<byte[]> videoStream = new List<byte[]>();

        static async Task Main(string[] args)
        {
            Console.WriteLine("[log] Reading config...");
            try
            {
                dynamic config = JsonConvert.DeserializeObject(File.ReadAllText("camera_config.json"));
                ISSUE_TOKEN = config.issue_token;
                COOKIE = config.cookie;
                API_KEY = config.api_key;
                USER_AGENT = config.user_agent;
                NEST_API_HOSTNAME = config.nest_api_hostname;
                CAMERA_API_HOSTNAME = config.camera_api_hostname;
                CAMERA_AUTH_COOKIE = config.camera_auth_cookie;

                Console.WriteLine("[log] Config loaded.");
            }
            catch
            {
                Console.WriteLine("[error] Could not read config.");
                Environment.Exit(1);
            }

            var token = await GetGoogleToken(ISSUE_TOKEN, COOKIE);

            if (!string.IsNullOrEmpty(token))
            {
                Console.WriteLine($"[log] Token succesfully obtained.");

                var data = await GetCameras(token);
                var camera = (dynamic)data;

                var nexusHost = (string)camera.items[0].direct_nexustalk_host;
                var cameraUuid = (string)camera.items[0].uuid;

                SetupConnection(nexusHost, cameraUuid, "37f678ad-eb69-0d80-705c-c921be02245f", token);

                while (!authorized)
                {
                    await Task.Delay(5);
                }

                while (true)
                {
                    StartPlayback(camera.items[0]);
                    await Task.Delay(35000);

                    List<byte[]> copyList = new List<byte[]>();
                    videoStream.ForEach(x => copyList.Add(x));
                    videoStream.Clear();

                    DumpToFile(copyList, DateTime.Now.ToString("yyyy-dd-M--HH-mm-ss") + ".mp4");
                }
            }
            else
            {
                Console.WriteLine("[error] Could not get the token.");
            }
        }

        static void DumpToFile(List<byte[]> buffer, string filename)
        {
            var startInfo = new ProcessStartInfo(@"D:\binaries\ready\ffmpeg.exe");
            startInfo.RedirectStandardInput = true;
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            startInfo.UseShellExecute = false;

            var argumentBuilder = new List<string>();
            argumentBuilder.Add("-loglevel panic");
            argumentBuilder.Add("-f h264");
            argumentBuilder.Add("-i pipe:");
            argumentBuilder.Add("-c:v libx264");
            argumentBuilder.Add("-bf 0");
            argumentBuilder.Add("-pix_fmt yuv420p");
            argumentBuilder.Add("-an");
            argumentBuilder.Add(filename);

            startInfo.Arguments = String.Join(" ", argumentBuilder.ToArray());

            var _ffMpegProcess = new Process();
            _ffMpegProcess.EnableRaisingEvents = true;
            _ffMpegProcess.OutputDataReceived += (s, e) => { Debug.WriteLine(e.Data); };
            _ffMpegProcess.ErrorDataReceived += (s, e) => { Debug.WriteLine(e.Data); };

            _ffMpegProcess.StartInfo = startInfo;

            Console.WriteLine($"[log] Starting write to {filename}...");

            _ffMpegProcess.Start();
            _ffMpegProcess.BeginOutputReadLine();
            _ffMpegProcess.BeginErrorReadLine();

            for (int i = 0; i < buffer.Count; i++)
            {
                _ffMpegProcess.StandardInput.BaseStream.Write(buffer[i], 0, buffer[i].Length);
            }

            _ffMpegProcess.StandardInput.BaseStream.Close();

            Console.WriteLine($"[log] Writing of {filename} completed.");
        }

        static void SetupConnection(string host, string cameraUuid, string deviceId, string token)
        {
            var tc = new TokenContainer();
            tc.olive_token = token;

            using (var mStream = new MemoryStream())
            {
                Serializer.Serialize(mStream, tc);

                var helloRequestBuffer = new HelloContainer();
                helloRequestBuffer.protocol_version = 3;
                helloRequestBuffer.uuid = cameraUuid;
                helloRequestBuffer.device_id = deviceId; // homebridge_uuid
                helloRequestBuffer.require_connected_camera = false;
                helloRequestBuffer.user_agent = USER_AGENT;
                helloRequestBuffer.client_type = 3;
                helloRequestBuffer.authorize_request = mStream.GetBuffer();

                using (var finalMStream = new MemoryStream())
                {
                    Serializer.Serialize(finalMStream, helloRequestBuffer);

                    var dataBuffer = PreformatData(PacketType.HELLO, finalMStream.ToArray());
                    var target = $"wss://{host}:80/nexustalk";
                    Console.WriteLine($"[log] Setting up connection to onnecting to {target}...");

                    ws = new WebSocket(target, sslProtocols: SslProtocols.Tls12 | SslProtocols.Tls11 | SslProtocols.Tls);
                    ws.EnableAutoSendPing = true;
                    ws.AutoSendPingInterval = 5;
                    ws.Security.AllowNameMismatchCertificate = true;
                    ws.Security.AllowUnstrustedCertificate = true;
                    ws.DataReceived += Ws_DataReceived;
                    ws.Error += Ws_Error;
                    ws.MessageReceived += Ws_MessageReceived;

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

            //string hex = BitConverter.ToString(finalBuffer);
            //string tdata = Encoding.ASCII.GetString(FromHex(hex));
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
            var headerLength = 0;
            uint length = 0;
            var type = 0;

            type = buffer[0];

            try
            {
                Debug.WriteLine("Received packed type: " + (PacketType)type);

                if ((PacketType)type == PacketType.LONG_PLAYBACK_PACKET)
                {
                    headerLength = 5;
                    var lengthBytes = new byte[4];
                    Buffer.BlockCopy(buffer, 1, lengthBytes, 0, lengthBytes.Length);
                    Array.Reverse(lengthBytes);
                    length = BitConverter.ToUInt32(lengthBytes);
                    Console.WriteLine("[log] Declared playback packet length: " + length);
                }
                else
                {
                    headerLength = 3;
                    var lengthBytes = new byte[2];
                    Buffer.BlockCopy(buffer, 1, lengthBytes, 0, lengthBytes.Length);
                    Array.Reverse(lengthBytes);
                    length = BitConverter.ToUInt16(lengthBytes);
                    Console.WriteLine("[log] Declared long playback packet length: " + length);
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

            StartPlayback sp = new StartPlayback();
            sp.session_id = new Random(745).Next(0, 100);
            sp.profile = (int)primaryProfile;
            sp.other_profiles = otherProfiles.ToArray<int>();

            using (MemoryStream spStream = new MemoryStream())
            {
                Serializer.Serialize(spStream, sp);
                var formattedSPOutput = PreformatData(PacketType.START_PLAYBACK, spStream.ToArray());
                ws.Send(formattedSPOutput, 0, formattedSPOutput.Length);
            }
        }

        private static void HandlePacketData(PacketType type, byte[] rawPayload)
        {
            switch (type)
            {
                case PacketType.OK:
                    authorized = true;
                    break;
                case PacketType.PING:
                    Console.WriteLine("[log] Ping.");
                    break;
                case PacketType.PLAYBACK_BEGIN:
                    HandlePlaybackBegin(rawPayload);
                    break;
                case PacketType.PLAYBACK_PACKET:
                    HandlePlayback(rawPayload);
                    break;
                default:
                    Console.WriteLine("[streamer] Unknown type.");
                    break;
            }
        }

        private static void HandlePlayback(byte[] rawPayload)
        {
            using (MemoryStream stream = new MemoryStream(rawPayload))
            {
                var packet = Serializer.Deserialize<PlaybackPacket>(stream);

                if (packet.channel_id == videoChannelId)
                {
                    byte[] h264Header = { 0x00, 0x00, 0x00, 0x01 };
                    var writingBlock = new byte[h264Header.Length + packet.payload.Length];
                    h264Header.CopyTo(writingBlock, 0);
                    packet.payload.CopyTo(writingBlock, h264Header.Length);

                    videoStream.Add(writingBlock);
                }
            }
        }

        private static void HandlePlaybackBegin(byte[] rawPayload)
        {
            using (MemoryStream stream = new MemoryStream(rawPayload))
            {
                var packet = Serializer.Deserialize<PlaybackBegin>(stream);

                foreach (var registeredStream in packet.channels)
                {
                    if ((CodecType)registeredStream.codec_type == CodecType.H264)
                    {
                        videoChannelId = registeredStream.codec_type;
                    }
                    else if ((CodecType)registeredStream.codec_type == CodecType.AAC)
                    {
                        audioChannelId = registeredStream.codec_type;
                    }
                }
            }
        }

        private static void Ws_MessageReceived(object sender, MessageReceivedEventArgs e)
        {
            Console.WriteLine("[log] Socket message received.");
        }

        private static void Ws_Error(object sender, SuperSocket.ClientEngine.ErrorEventArgs e)
        {
            Console.WriteLine("[log] Socket errored out.");
            Console.WriteLine(e.Exception.Message);
            Console.WriteLine(e.Exception.InnerException);
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

        static async Task<string> GetGoogleToken(string issueToken, string cookie)
        {
            var tokenUri = new Uri(issueToken);
            var referrerDomain = string.Empty;

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
