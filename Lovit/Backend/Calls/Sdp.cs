using System;
using System.Collections.Generic;
using System.Text;

namespace Lovit.Backend.Calls
{
    /// <summary>Кандидат ICE собеседника — то, что нужно ядру: UDP, IPv4.</summary>
    public sealed class RemoteCandidate
    {
        public string Ip { get; set; }
        public int Port { get; set; }
        public uint Priority { get; set; }
        public string Type { get; set; }

        /// <summary>
        /// Строка кандидата («candidate:… typ host …»). null — не наш случай:
        /// TCP, IPv6, второй компонент или имя вместо адреса.
        /// </summary>
        public static RemoteCandidate Parse(string line)
        {
            if (string.IsNullOrEmpty(line)) return null;

            string text = line.Trim();
            if (text.StartsWith("a=")) text = text.Substring(2);
            if (text.StartsWith("candidate:")) text = text.Substring("candidate:".Length);

            string[] parts = text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 8 || parts[6] != "typ") return null;
            if (parts[1] != "1") return null;
            if (!string.Equals(parts[2], "udp", StringComparison.OrdinalIgnoreCase)) return null;

            string ip = parts[4];
            if (ip.Contains(":") || ip.Split('.').Length != 4) return null;

            int port;
            uint priority;
            if (!int.TryParse(parts[5], out port) || port <= 0) return null;
            uint.TryParse(parts[3], out priority);

            return new RemoteCandidate { Ip = ip, Port = port, Priority = priority, Type = parts[7] };
        }

        public override string ToString()
        {
            return Type + " " + Ip + ":" + Port;
        }
    }

    /// <summary>
    /// Предложение звонящего (SDP offer) — то немногое, что из него нужно.
    ///
    /// Звонящий — телефон с официальным MAX, то есть обычный libwebrtc:
    /// unified plan, bundle, rtcp-mux. Секций бывает несколько — звук, видео
    /// (даже в голосовом звонке: на случай, если включат камеру), иногда
    /// канал данных. Мы принимаем только звук, остальное отклоняем нулевым
    /// портом — так велит RFC 3264, и libwebrtc это понимает.
    /// </summary>
    public sealed class SdpOffer
    {
        public sealed class Section
        {
            public string Media { get; set; }      // audio, video, application
            public string Mid { get; set; }
            public string Protocol { get; set; }
            public string Formats { get; set; }
        }

        public string Ufrag { get; private set; }
        public string Password { get; private set; }
        public string Fingerprint { get; private set; }
        /// <summary>a=setup собеседника: actpass в предложении, active или passive в ответе.</summary>
        public string Setup { get; private set; }
        public int OpusPt { get; private set; }
        public string AudioMid { get; private set; }
        public List<Section> Sections { get; private set; }
        public List<RemoteCandidate> Candidates { get; private set; }

        /// <summary>Разобрать. null — без звука, ICE или отпечатка звонок не принять.</summary>
        public static SdpOffer Parse(string sdp)
        {
            if (string.IsNullOrEmpty(sdp)) return null;

            var offer = new SdpOffer
            {
                Sections = new List<Section>(),
                Candidates = new List<RemoteCandidate>(),
                OpusPt = -1
            };

            Section current = null;
            int audioOpus = -1;

            foreach (string raw in sdp.Split('\n'))
            {
                string line = raw.TrimEnd('\r');

                if (line.StartsWith("m="))
                {
                    string[] parts = line.Substring(2).Split(new[] { ' ' }, 4);
                    current = new Section
                    {
                        Media = parts[0],
                        Protocol = parts.Length > 2 ? parts[2] : "UDP/TLS/RTP/SAVPF",
                        Formats = parts.Length > 3 ? parts[3] : "0"
                    };
                    offer.Sections.Add(current);
                    continue;
                }

                // Логин, пароль и отпечаток бывают и на уровне сеанса, и в
                // секции — при bundle они одинаковы, берём первые попавшиеся.
                if (line.StartsWith("a=ice-ufrag:") && offer.Ufrag == null)
                    offer.Ufrag = line.Substring("a=ice-ufrag:".Length).Trim();
                else if (line.StartsWith("a=ice-pwd:") && offer.Password == null)
                    offer.Password = line.Substring("a=ice-pwd:".Length).Trim();
                else if (line.StartsWith("a=setup:") && offer.Setup == null)
                    offer.Setup = line.Substring("a=setup:".Length).Trim();
                else if (line.StartsWith("a=fingerprint:sha-256 ") && offer.Fingerprint == null)
                    offer.Fingerprint = line.Substring("a=fingerprint:sha-256 ".Length).Trim();
                else if (line.StartsWith("a=mid:") && current != null)
                    current.Mid = line.Substring("a=mid:".Length).Trim();
                else if (line.StartsWith("a=rtpmap:") && current != null && current.Media == "audio")
                {
                    // a=rtpmap:111 opus/48000/2
                    string rest = line.Substring("a=rtpmap:".Length);
                    int space = rest.IndexOf(' ');
                    int pt;
                    if (space > 0 && rest.Substring(space + 1).StartsWith("opus/48000", StringComparison.OrdinalIgnoreCase)
                        && int.TryParse(rest.Substring(0, space), out pt) && audioOpus < 0)
                    {
                        audioOpus = pt;
                    }
                }
                else if (line.StartsWith("a=candidate:"))
                {
                    RemoteCandidate candidate = RemoteCandidate.Parse(line);
                    if (candidate != null) offer.Candidates.Add(candidate);
                }
            }

            foreach (Section section in offer.Sections)
            {
                if (section.Media == "audio" && audioOpus >= 0)
                {
                    offer.AudioMid = section.Mid ?? "0";
                    break;
                }
            }

            offer.OpusPt = audioOpus;

            if (offer.Ufrag == null || offer.Password == null || offer.Fingerprint == null
                || offer.OpusPt < 0 || offer.AudioMid == null)
            {
                return null;
            }
            return offer;
        }

        /// <summary>Номер Opus в нашем предложении — как у libwebrtc.</summary>
        public const int OurOpusPt = 111;

        /// <summary>
        /// Наше предложение, когда звоним сами: один звук, Opus, роль DTLS
        /// оставляем на выбор собеседнику (`actpass`) — libwebrtc в ответе
        /// обычно берёт `active`, и тогда сервер DTLS — мы.
        /// </summary>
        internal static string BuildOffer(CallCore core)
        {
            var sdp = new StringBuilder();
            Action<string> line = text => sdp.Append(text).Append("\r\n");

            line("v=0");
            line("o=- " + DateTime.UtcNow.Ticks + " 2 IN IP4 127.0.0.1");
            line("s=-");
            line("t=0 0");
            line("a=group:BUNDLE 0");
            line("a=msid-semantic: WMS lovit");
            line("m=audio 9 UDP/TLS/RTP/SAVPF " + OurOpusPt);
            line("c=IN IP4 0.0.0.0");
            line("a=rtcp:9 IN IP4 0.0.0.0");
            line("a=ice-ufrag:" + core.Ufrag);
            line("a=ice-pwd:" + core.Password);
            line("a=fingerprint:sha-256 " + core.Fingerprint);
            line("a=setup:actpass");
            line("a=mid:0");
            line("a=sendrecv");
            line("a=msid:lovit audio0");
            line("a=rtcp-mux");
            line("a=rtpmap:" + OurOpusPt + " opus/48000/2");
            line("a=fmtp:" + OurOpusPt + " minptime=10;useinbandfec=1");
            line("a=ssrc:" + core.Ssrc + " cname:lovit");
            line("a=ssrc:" + core.Ssrc + " msid:lovit audio0");
            return sdp.ToString();
        }

        /// <summary>
        /// Наш ответ. Один звук, мы — клиент DTLS (`setup:active`): так нам
        /// проще, а звонящий в предложении всегда пишет `actpass`.
        /// </summary>
        internal string BuildAnswer(CallCore core)
        {
            var sdp = new StringBuilder();
            Action<string> line = text => sdp.Append(text).Append("\r\n");

            line("v=0");
            line("o=- " + DateTime.UtcNow.Ticks + " 2 IN IP4 127.0.0.1");
            line("s=-");
            line("t=0 0");
            line("a=group:BUNDLE " + AudioMid);
            line("a=msid-semantic: WMS lovit");

            bool audioDone = false;

            foreach (Section section in Sections)
            {
                if (section.Media == "audio" && !audioDone && (section.Mid ?? "0") == AudioMid)
                {
                    audioDone = true;

                    line("m=audio 9 UDP/TLS/RTP/SAVPF " + OpusPt);
                    line("c=IN IP4 0.0.0.0");
                    line("a=rtcp:9 IN IP4 0.0.0.0");
                    line("a=ice-ufrag:" + core.Ufrag);
                    line("a=ice-pwd:" + core.Password);
                    line("a=fingerprint:sha-256 " + core.Fingerprint);
                    line("a=setup:active");
                    line("a=mid:" + AudioMid);
                    line("a=sendrecv");
                    line("a=msid:lovit audio0");
                    line("a=rtcp-mux");
                    line("a=rtpmap:" + OpusPt + " opus/48000/2");
                    line("a=fmtp:" + OpusPt + " minptime=10;useinbandfec=1");
                    line("a=ssrc:" + core.Ssrc + " cname:lovit");
                    line("a=ssrc:" + core.Ssrc + " msid:lovit audio0");
                    continue;
                }

                // Остальное отклоняем: нулевой порт и тот же mid.
                line("m=" + section.Media + " 0 " + section.Protocol + " " + section.Formats);
                line("c=IN IP4 0.0.0.0");
                if (section.Mid != null) line("a=mid:" + section.Mid);
                if (section.Media != "application") line("a=inactive");
            }

            return sdp.ToString();
        }
    }
}
