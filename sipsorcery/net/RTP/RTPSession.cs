﻿//-----------------------------------------------------------------------------
// Filename: RTPSession.cs
//
// Description: Represents an RTP session constituted of a single media stream. The session
// does not control the sockets as they may be shared by multiple sessions.
//
// Author(s):
// Aaron Clauson (aaron@sipsorcery.com)
//
// History:
// 25 Aug 2019	Aaron Clauson	Created, Montreux, Switzerland.
// 12 Nov 2019  Aaron Clauson   Added send event method.
// 07 Dec 2019  Aaron Clauson   Big refactor. Brought in a lot of functions previously
//                              in the RTPChannel class.
//
// License: 
// BSD 3-Clause "New" or "Revised" License, see included LICENSE.md file.
//-----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SIPSorcery.Sys;

namespace SIPSorcery.Net
{
    public delegate int ProtectRtpPacket(byte[] payload, int length, out int outputBufferLength);

    public enum SetDescriptionResultEnum
    {
        OK,
        AudioIncompatible,
        VideoIncompatible
    }

    public class MediaStreamTrack
    {
        /// <summary>
        /// The type of media stream represented by this track. Must be audio or video.
        /// </summary>
        public SDPMediaTypesEnum Kind { get; private set; }

        /// <summary>
        /// The value used in the RTP Synchronisation Source header field for media packets
        /// sent using this media stream.
        /// </summary>
        public uint Ssrc { get; internal set; }

        /// <summary>
        /// The value used in the RTP Sequence Number header field for media packets
        /// sent using this media stream.
        /// </summary>
        public ushort SeqNum { get; internal set; }

        /// <summary>
        /// The value used in the RTP Timestamp header field for media packets
        /// sent using this media stream.
        /// </summary>
        public uint Timestamp { get; internal set; }

        /// <summary>
        /// Media ID. Used for setting the "mid" attribute in SDP descriptions.
        /// </summary>
        public string MID { get; private set; }

        /// <summary>
        /// Indicates whether this track was sourced by a remote connection.
        /// </summary>
        public bool IsRemote { get; set; }

        /// <summary>
        /// The media capabilities supported by this track.
        /// </summary>
        public List<SDPMediaFormat> Capabilties { get; internal set; }

        /// <summary>
        /// Holds the stream state of the track.
        /// </summary>
        public MediaStreamStatusEnum StreamStatus { get; internal set; }

        /// <summary>
        /// Creates a lightweight class to track a media stream track within an RTP session 
        /// When supporting RFC3550 (the standard RTP specification) the relationship between
        /// an RTP stream and session is 1:1. For WebRTC and RFC8101 there can be multiple
        /// streams per session.
        /// </summary>
        /// <param name="mid">The media ID for this track. Must match the value set in the SDP.</param>
        /// <param name="kind">The type of media for this stream. There can only be one
        /// stream per media type.</param>
        /// <param name="isRemote">True if this track corresponds to a media announcement from the 
        /// remote party.</param>
        /// <param name="capabilties">The capabilities for the track being added. Where the same media
        /// type is supported locally and remotely only the mutual capabilities can be used. This will
        /// occur if we receive an SDP offer (add track initiated by the remote party) and we need
        /// to remove capabilities we don't support.</param>
        /// <param name="streamStatus">The initial stream status for the media track. Defaults to
        /// send receive.</param>
        public MediaStreamTrack(
            SDPMediaTypesEnum kind, 
            bool isRemote, 
            List<SDPMediaFormat> capabilties,
            MediaStreamStatusEnum streamStatus = MediaStreamStatusEnum.SendRecv)
        {
            Kind = kind;
            IsRemote = isRemote;
            Capabilties = capabilties;
            StreamStatus = streamStatus;

            if (!isRemote)
            {
                Ssrc = Convert.ToUInt32(Crypto.GetRandomInt(0, Int32.MaxValue));
                SeqNum = Convert.ToUInt16(Crypto.GetRandomInt(0, UInt16.MaxValue));
            }
        }

        /// <summary>
        /// Additional constructor that allows the Media ID to be set.
        /// </summary>
        public MediaStreamTrack(
            string mediaID, 
            SDPMediaTypesEnum kind, 
            bool isRemote, 
            List<SDPMediaFormat> capabilties,
            MediaStreamStatusEnum streamStatus = MediaStreamStatusEnum.SendRecv) :
            this(kind, isRemote, capabilties, streamStatus)
        {
            MID = mediaID;
        }

        /// <summary>
        /// Checks whether the payload ID in an RTP packet received from the remote call party
        /// is in this track's list.
        /// </summary>
        /// <param name="payloadID">The payload ID to check against.</param>
        /// <returns>True if the payload ID matches one of the codecs for this stream. False if not.</returns>
        public bool IsPayloadIDMatch(int payloadID)
        {
            return Capabilties.Any(x => x.FormatID == payloadID.ToString());
        }

        /// <summary>
        /// Creates and returns a copy of the media stream track.
        /// </summary>
        public MediaStreamTrack CopyOf()
        {
            List<SDPMediaFormat> capabilties = new List<SDPMediaFormat>(Capabilties);
            var copy = new MediaStreamTrack(MID, Kind, IsRemote, capabilties, StreamStatus);
            copy.Ssrc = Ssrc;
            copy.SeqNum = SeqNum;
            copy.Timestamp = Timestamp;
            return copy;
        }
    }

    /// <summary>
    /// The RTPSession class is the primary point for interacting with the Real-Time
    /// Protocol. It manages all the resources required for setting up and then sending
    /// and receiving RTP packets. This class IS designed to be inherited by child 
    /// classes and for child classes to add audio and video processing logic.
    /// </summary>
    /// <remarks>
    /// The setting up of an RTP stream involved the exchange of Session Descriptions 
    /// (SDP) with the remote party. This class has adopted the mechanism used by WebRTC.
    /// The steps are:
    /// 1. If acting as the initiator:
    ///   a. Create offer,
    ///   b. Send offer to remote party and get their answer (external to this class, requires signalling),
    ///   c. Set remote description,
    ///   d. Optionally perform any additional set up, such as negotiating SRTP keying material,
    ///   e. Call Start to commence RTCP reporting.
    /// 2. If acting as the recipient:
    ///   a. Receive offer,
    ///   b. Set remote description. This step MUST be done before an SDP answer can be generated.
    ///      This step can also result in an error condition if the codecs/formats offered aren't supported,
    ///   c. Create answer,
    ///   d. Send answer to remote party (external to this class, requires signalling),
    ///   e. Optionally perform any additional set up, such as negotiating SRTP keying material,
    ///   f. Call Start to commence RTCP reporting.
    /// </remarks>
    public class RTPSession : IDisposable
    {
        private const int RTP_MAX_PAYLOAD = 1400;

        /// <summary>
        /// From libsrtp: SRTP_MAX_TRAILER_LEN is the maximum length of the SRTP trailer
        /// (authentication tag and MKI) supported by libSRTP.This value is
        /// the maximum number of octets that will be added to an RTP packet by
        /// srtp_protect().
        /// 
        /// srtp_protect():
        /// @warning This function assumes that it can write SRTP_MAX_TRAILER_LEN
        /// into the location in memory immediately following the RTP packet.
        /// Callers MUST ensure that this much writable memory is available in
        /// the buffer that holds the RTP packet.
        /// 
        /// srtp_protect_rtcp():
        /// @warning This function assumes that it can write SRTP_MAX_TRAILER_LEN+4
        /// to the location in memory immediately following the RTCP packet.
        /// Callers MUST ensure that this much writable memory is available in
        /// the buffer that holds the RTCP packet.
        /// </summary>
        public const int SRTP_MAX_PREFIX_LENGTH = 148;
        private const int DEFAULT_AUDIO_CLOCK_RATE = 8000;
        public const int H264_RTP_HEADER_LENGTH = 2;
        public const int RTP_EVENT_DEFAULT_SAMPLE_PERIOD_MS = 50; // Default sample period for an RTP event as specified by RFC2833.
        public const SDPMediaTypesEnum DEFAULT_MEDIA_TYPE = SDPMediaTypesEnum.audio; // If we can't match an RTP payload ID assume it's audio.
        public const int DEFAULT_DTMF_EVENT_PAYLOAD_ID = 101;
        private const string RTP_MEDIA_PROFILE = "RTP/AVP";

        private static ILogger logger = Log.Logger;

        private bool m_isMediaMultiplexed = false;      // Indicates whether audio and video are multiplexed on a single RTP channel or not.
        private bool m_isRtcpMultiplexed = false;       // Indicates whether the RTP channel is multiplexing RTP and RTCP packets on the same port.
        private bool m_rtpEventInProgress;              // Gets set to true when an RTP event is being sent and the normal stream is interrupted.
        private uint m_lastRtpTimestamp;                 // The last timestamp used in an RTP packet.    
        private bool m_isClosed;
        private bool m_isStarted;

        internal List<MediaStreamTrack> m_tracks = new List<MediaStreamTrack>();
        internal Dictionary<SDPMediaTypesEnum, RTPChannel> m_rtpChannels = new Dictionary<SDPMediaTypesEnum, RTPChannel>();

        /// <summary>
        /// The local audio stream for this session. Will be null if we are not sending audio.
        /// </summary>
        internal MediaStreamTrack AudioLocalTrack
        {
            get { return m_tracks.SingleOrDefault(x => x.Kind == SDPMediaTypesEnum.audio && !x.IsRemote); }
        }

        /// <summary>
        /// The remote audio track for this session. Will be null if the remote party is not sending audio.
        /// </summary>
        internal MediaStreamTrack AudioRemoteTrack
        {
            get { return m_tracks.SingleOrDefault(x => x.Kind == SDPMediaTypesEnum.audio && x.IsRemote); }
        }

        /// <summary>
        /// The reporting session for the audio stream. Will be null if only video is being sent.
        /// </summary>
        private RTCPSession m_audioRtcpSession;

        /// <summary>
        /// The local video track for this session. Will be null if we are not sending video.
        /// </summary>
        internal MediaStreamTrack VideoLocalTrack
        {
            get { return m_tracks.SingleOrDefault(x => x.Kind == SDPMediaTypesEnum.video && !x.IsRemote); }
        }

        /// <summary>
        /// The remote video track for this session. Will be null if the remote party is not sending video.
        /// </summary>
        internal MediaStreamTrack VideoRemoteTrack
        {
            get { return m_tracks.SingleOrDefault(x => x.Kind == SDPMediaTypesEnum.video && x.IsRemote); }
        }

        /// <summary>
        /// The reporting session for the video stream. Will be null if only audio is being sent.
        /// </summary>
        private RTCPSession m_videoRtcpSession;

        /// <summary>
        /// The SDP offered by the remote call party for this session.
        /// </summary>
        public SDP RemoteDescription { get; protected set; }

        /// <summary>
        /// Function pointer to an SRTP context that encrypts an RTP packet.
        /// </summary>
        private ProtectRtpPacket m_srtpProtect;

        /// <summary>
        /// Function pointer to an SRTP context that decrypts an RTP packet.
        /// </summary>
        private ProtectRtpPacket m_srtpUnprotect;

        /// <summary>
        /// Function pointer to an SRTCP context that encrypts an RTP packet.
        /// </summary>
        private ProtectRtpPacket m_srtcpControlProtect;

        /// <summary>
        /// Function pointer to an SRTP context that decrypts an RTP packet.
        /// </summary>
        private ProtectRtpPacket m_srtcpControlUnprotect;

        /// <summary>
        /// Indicates whether this session is using a secure SRTP context to encrypt RTP and
        /// RTCP packets.
        /// </summary>
        public bool IsSecure { get; private set; } = false;

        /// <summary>
        /// If this session is using a secure context this flag MUST be set to indicate
        /// the security delegate (SrtpProtect, SrtpUnprotect etc) have been set.
        /// </summary>
        public bool IsSecureContextReady { get; private set; } = false;

        /// <summary>
        /// The remote RTP end point this session is sending audio to.
        /// </summary>
        public IPEndPoint AudioDestinationEndPoint { get; private set; }

        /// <summary>
        /// The remote RTP control end point this session is sending to RTCP reports 
        /// for the audio stream to.
        /// </summary>
        public IPEndPoint AudioControlDestinationEndPoint { get; private set; }

        /// <summary>
        /// The remote RTP end point this session is sending video to.
        /// </summary>
        public IPEndPoint VideoDestinationEndPoint { get; private set; }

        /// <summary>
        /// The remote RTP control end point this session is sending to RTCP reports 
        /// for the video stream to.
        /// </summary>
        public IPEndPoint VideoControlDestinationEndPoint { get; private set; }

        /// <summary>
        /// In order to detect RTP events from the remote party this property needs to 
        /// be set to the payload ID they are using.
        /// </summary>
        public int RemoteRtpEventPayloadID { get; set; } = DEFAULT_DTMF_EVENT_PAYLOAD_ID;

        public bool IsClosed { get { return m_isClosed; } }

        /// <summary>
        /// Indicates whether this session is using audio.
        /// </summary>
        public bool HasAudio
        {
            get { return m_tracks.Any(x => x.Kind == SDPMediaTypesEnum.audio); }
        }

        /// <summary>
        /// Indicates whether this session is using video.
        /// </summary>
        public bool HasVideo
        {
            get { return m_tracks.Any(x => x.Kind == SDPMediaTypesEnum.video); }
        }

        /// <summary>
        /// Gets fired when an RTP packet is received from a remote party.
        /// </summary>
        public event Action<SDPMediaTypesEnum, RTPPacket> OnRtpPacketReceived;

        /// <summary>
        /// Gets fired when an RTP event is detected on the remote call party's RTP stream.
        /// </summary>
        public event Action<RTPEvent, RTPHeader> OnRtpEvent;

        /// <summary>
        /// Gets fired when the RTP session and underlying channel are closed.
        /// </summary>
        public event Action<string> OnRtpClosed;

        /// <summary>
        /// Gets fired when an RTCP BYE packet is received from the remote party.
        /// The string parameter contains the BYE reason. Normally a BYE
        /// report means the RTP session is finished. But... cases have been observed where
        /// an RTCP BYE is received when a remote party is put on hold and then the session
        /// resumes when take off hold. It's up to the application to decide what action to
        /// take when n RTCP BYE is received.
        /// </summary>
        public event Action<string> OnRtcpBye;

        /// <summary>
        /// Fires when the connection for a media type is classified as timed out due to not
        /// receiving any RTP or RTCP packets within the given period.
        /// </summary>
        public event Action<SDPMediaTypesEnum> OnTimeout;

        /// <summary>
        /// Gets fired when an RTCP report is received. This event is for diagnostics only.
        /// </summary>
        public event Action<SDPMediaTypesEnum, RTCPCompoundPacket> OnReceiveReport;

        /// <summary>
        /// Gets fired when an RTCP report is sent. This event is for diagnostics only.
        /// </summary>
        public event Action<SDPMediaTypesEnum, RTCPCompoundPacket> OnSendReport;

        /// <summary>
        /// Creates a new RTP session. The synchronisation source and sequence number are initialised to
        /// pseudo random values.
        /// </summary>
        /// <param name="isRtcpMultiplexed">If true RTCP reports will be multiplexed with RTP on a single channel.
        /// If false (standard mode) then a separate socket is used to send and receive RTCP reports.</param>
        /// <param name="isSecure">If true indicated this session is using SRTP to encrypt and authorise
        /// RTP and RTCP packets. No communications or reporting will commence until the 
        /// is explicitly set as complete.</param>
        public RTPSession(
            bool isMediaMultiplexed,
            bool isRtcpMultiplexed,
            bool isSecure)
        {
            m_isMediaMultiplexed = isMediaMultiplexed;
            m_isRtcpMultiplexed = isRtcpMultiplexed;
            IsSecure = isSecure;
        }

        /// <summary>
        /// Used for child classes that require a single RTP channel for all RTP (audio and video)
        /// and RTCP communications.
        /// </summary>
        protected void addSingleTrack()
        {
            // We use audio as the media type when multiplexing.
            CreateRtpChannel(SDPMediaTypesEnum.audio);
            m_audioRtcpSession = CreateRtcpSession(SDPMediaTypesEnum.audio);
        }

        /// <summary>
        /// Adds a media track to this session. A media track represents an audio or video
        /// stream and can be a local (which means we're sending) or remote (which means
        /// we're receiving).
        /// </summary>
        /// <param name="track">The media track to add to the session.</param>
        public virtual void addTrack(MediaStreamTrack track)
        {
            if (m_isMediaMultiplexed && m_rtpChannels.Count == 0)
            {
                // We use audio as the media type when multiplexing.
                CreateRtpChannel(SDPMediaTypesEnum.audio);
                m_audioRtcpSession = CreateRtcpSession(SDPMediaTypesEnum.audio);
            }

            if (track.Kind == SDPMediaTypesEnum.audio)
            {
                if (!m_isMediaMultiplexed && !m_rtpChannels.ContainsKey(SDPMediaTypesEnum.audio))
                {
                    CreateRtpChannel(SDPMediaTypesEnum.audio);
                }

                if (m_audioRtcpSession == null)
                {
                    m_audioRtcpSession = CreateRtcpSession(SDPMediaTypesEnum.audio);
                }

                if (!track.IsRemote)
                {
                    if (AudioLocalTrack != null)
                    {
                        throw new ApplicationException("A local audio track has already been set on this session.");
                    }
                    else
                    {
                        // Need to create a sending SSRC and set it on the RTCP session. 
                        m_audioRtcpSession.Ssrc = track.Ssrc;
                        m_tracks.Add(track);
                    }
                }
                else
                {
                    if (AudioRemoteTrack != null)
                    {
                        throw new ApplicationException("A remote audio track has already been set on this session.");
                    }
                    else
                    {
                        m_tracks.Add(track);
                    }
                }
            }
            else if (track.Kind == SDPMediaTypesEnum.video)
            {
                if (!m_isMediaMultiplexed && !m_rtpChannels.ContainsKey(SDPMediaTypesEnum.video))
                {
                    CreateRtpChannel(SDPMediaTypesEnum.video);
                }

                if (m_videoRtcpSession == null)
                {
                    m_videoRtcpSession = CreateRtcpSession(SDPMediaTypesEnum.video);
                }

                if (!track.IsRemote)
                {
                    if (VideoLocalTrack != null)
                    {
                        throw new ApplicationException("A local video track has already been set on this session.");
                    }
                    else
                    {
                        // Need to create a sending SSRC and set it on the RTCP session. 
                        m_videoRtcpSession.Ssrc = track.Ssrc;
                        m_tracks.Add(track);
                    }
                }
                else
                {
                    if (VideoRemoteTrack != null)
                    {
                        throw new ApplicationException("A remote video track has already been set on this session.");
                    }
                    else
                    {
                        m_tracks.Add(track);
                    }
                }
            }
        }

        /// <summary>
        /// Generates the SDP for an offer that can be made to a remote user agent.
        /// </summary>
        /// <param name="connectionAddress">Optional. If specified this IP address
        /// will be used as the address advertised in the SDP offer. If not provided
        /// the kernel routing table will be used to determine the local IP address used
        /// for Internet access.</param>
        /// <returns>A task that when complete contains the SDP offer.</returns>
        public SDP CreateOffer(IPAddress connectionAddress)
        {
            if (AudioLocalTrack == null && VideoLocalTrack == null)
            {
                logger.LogWarning("No local media tracks available for create offer.");
                return null;
            }
            else
            {
                var offerSdp = GetSessionDesciption(m_tracks.Where(x => x.IsRemote == false).ToList(), connectionAddress);
                return offerSdp;
            }
        }

        /// <summary>
        /// Generates an SDP answer in response to an offer. The remote description MUST be set 
        /// prior to calling this method.
        /// </summary>
        /// <param name="connectionAddress">Optional. If set this address will be used as 
        /// the SDP Connection address. If not specified the Operating System routing table
        /// will be used to lookup the address used to connect to the SDP connection address
        /// from the remote offer.</param>
        /// <returns>A task that when complete contains the SDP answer.</returns>
        /// <remarks>As specified in https://tools.ietf.org/html/rfc3264#section-6.1.
        ///  "If the answerer has no media formats in common for a particular
        ///   offered stream, the answerer MUST reject that media stream by setting
        ///   the port to zero."
        /// </remarks>
        public SDP CreateAnswer(IPAddress connectionAddress)
        {
            if (RemoteDescription == null)
            {
                throw new ApplicationException("The remote description is not set, cannot create SDP answer.");
            }
            else
            {
                var offer = RemoteDescription;

                List<MediaStreamTrack> tracks = new List<MediaStreamTrack>();

                // The order of the announcements in the answer must match the order in the offer.
                foreach (var announcement in offer.Media)
                {
                    // Adjust the local audio tracks to only include compatible capabilities.
                    if (announcement.Media == SDPMediaTypesEnum.audio)
                    {
                        if (AudioLocalTrack != null)
                        {
                            var audioCopy = AudioLocalTrack.CopyOf();
                            var remoteAudioFormats = offer.Media.Where(x => x.Media == SDPMediaTypesEnum.audio).Single().MediaFormats;
                            var audioCompatibleFormats = SDPMediaFormat.GetCompatibleFormats(AudioLocalTrack.Capabilties, remoteAudioFormats);

                            // Set the capabilities on our audio track to the ones the remote party can use.
                            audioCopy.Capabilties = audioCompatibleFormats;

                            tracks.Add(audioCopy);
                        }
                        else
                        {
                            // Audio support is not available locally. The media announcement still needs to be set in the answer with a port
                            // of zero.
                            var noAudioTrack = new MediaStreamTrack(SDPMediaTypesEnum.audio, false, null);
                            tracks.Add(noAudioTrack);
                        }
                    }
                    else if (announcement.Media == SDPMediaTypesEnum.video)
                    {
                        if (VideoLocalTrack != null)
                        {
                            var videoCopy = VideoLocalTrack.CopyOf();
                            var remoteVideoFormats = offer.Media.Where(x => x.Media == SDPMediaTypesEnum.video).Single().MediaFormats;
                            var videoCompatibleFormats = SDPMediaFormat.GetCompatibleFormats(VideoLocalTrack.Capabilties, remoteVideoFormats);

                            // Set the capabilities on our video track to the ones the remote party can use.
                            videoCopy.Capabilties = videoCompatibleFormats;

                            tracks.Add(videoCopy);
                        }
                        else
                        {
                            // Video support is not available locally. The media announcement still needs to be set in the answer with a port
                            // of zero.
                            var noVideoTrack = new MediaStreamTrack(SDPMediaTypesEnum.video, false, null);
                            tracks.Add(noVideoTrack);
                        }
                    }
                }

                if (connectionAddress == null)
                {
                    // No specific connection address supplied. Lookup the local address to connect to the offer address.
                    var offerConnectionAddress = (offer.Connection?.ConnectionAddress != null) ? IPAddress.Parse(offer.Connection.ConnectionAddress) : null;

                    if (offerConnectionAddress == null || offerConnectionAddress == IPAddress.Any || offerConnectionAddress == IPAddress.IPv6Any)
                    {
                        connectionAddress = NetServices.InternetDefaultAddress;
                    }
                    else
                    {
                        connectionAddress = NetServices.GetLocalAddressForRemote(offerConnectionAddress);
                    }
                }

                var answerSdp = GetSessionDesciption(tracks, connectionAddress);

                return answerSdp;
            }
        }

        /// <summary>
        /// Sets the remote SDP description for this session.
        /// </summary>
        /// <param name="sessionDescription">The SDP that will be set as the remote description.</param>
        /// <returns>If successful an OK enum result. If not an enum result indicating the failure cause.</returns>
        public SetDescriptionResultEnum SetRemoteDescription(SDP sessionDescription)
        {
            IPAddress connectionAddress = null;

            if (sessionDescription.Connection != null && !String.IsNullOrEmpty(sessionDescription.Connection.ConnectionAddress))
            {
                connectionAddress = IPAddress.Parse(sessionDescription.Connection.ConnectionAddress);
            }
            else
            {
                logger.LogWarning("RTP session set remote description was supplied an SDP with no connection address.");
            }

            IPEndPoint remoteAudioRtpEP = null;
            IPEndPoint remoteAudioRtcpEP = null;
            IPEndPoint remoteVideoRtpEP = null;
            IPEndPoint remoteVideoRtcpEP = null;

            foreach (var announcement in sessionDescription.Media)
            {
                if (announcement.Media == SDPMediaTypesEnum.audio)
                {
                    var audioAnnounce = announcement;

                    // Check that there is at least one compatible non-"RTP Event" audio codec.
                    var audioCompatibleFormats = SDPMediaFormat.GetCompatibleFormats(AudioLocalTrack.Capabilties, audioAnnounce.MediaFormats);
                    if(audioCompatibleFormats?.Count == 0)
                    {
                        return SetDescriptionResultEnum.AudioIncompatible;
                    }

                    // If there's an existing remote audio track it needs to be replaced.
                    var existingRemoteAudioTrack = m_tracks.Where(x => x.Kind == SDPMediaTypesEnum.audio && x.IsRemote).SingleOrDefault();
                    if (existingRemoteAudioTrack != null)
                    {
                        logger.LogDebug("Removing existing remote audio track.");
                        m_tracks.Remove(existingRemoteAudioTrack);
                    }

                    logger.LogDebug("Adding remote audio track to session.");

                    var remoteAudioTrack = new MediaStreamTrack(SDPMediaTypesEnum.audio, true, audioAnnounce.MediaFormats);
                    addTrack(remoteAudioTrack);

                    var audioAddr = (audioAnnounce.Connection != null) ? IPAddress.Parse(audioAnnounce.Connection.ConnectionAddress) : connectionAddress;

                    if (audioAddr != null)
                    {
                        remoteAudioRtpEP = new IPEndPoint(audioAddr, audioAnnounce.Port);
                        remoteAudioRtcpEP = new IPEndPoint(audioAddr, audioAnnounce.Port + 1);
                    }

                    foreach (var mediaFormat in audioAnnounce.MediaFormats)
                    {
                        if (mediaFormat.FormatAttribute?.StartsWith(SDP.TELEPHONE_EVENT_ATTRIBUTE) == true)
                        {
                            if (int.TryParse(mediaFormat.FormatID, out var remoteRtpEventPayloadID))
                            {
                                RemoteRtpEventPayloadID = remoteRtpEventPayloadID;
                            }
                            break;
                        }
                    }
                }
                else if (announcement.Media == SDPMediaTypesEnum.video)
                {
                    var videoAnnounce = announcement;

                    // Check that there is at least one compatible non-"RTP Event" video codec.
                    var videoCompatibleFormats = SDPMediaFormat.GetCompatibleFormats(VideoLocalTrack.Capabilties, videoAnnounce.MediaFormats);
                    if (videoCompatibleFormats?.Count == 0)
                    {
                        return SetDescriptionResultEnum.VideoIncompatible;
                    }

                    // If there's an existing remote video track it needs to be replaced.
                    var existingRemoteVideoTrack = m_tracks.Where(x => x.Kind == SDPMediaTypesEnum.video && x.IsRemote).SingleOrDefault();
                    if (existingRemoteVideoTrack != null)
                    {
                        logger.LogDebug("Removing existing remote video track.");
                        m_tracks.Remove(existingRemoteVideoTrack);
                    }

                    logger.LogDebug("Adding remote video track to session.");

                    var remoteVideoTrack = new MediaStreamTrack(SDPMediaTypesEnum.video, true, videoAnnounce.MediaFormats);
                    addTrack(remoteVideoTrack);

                    var videoAddr = (videoAnnounce.Connection != null) ? IPAddress.Parse(videoAnnounce.Connection.ConnectionAddress) : connectionAddress;

                    if (videoAddr != null)
                    {
                        remoteVideoRtpEP = new IPEndPoint(videoAddr, videoAnnounce.Port);
                        remoteVideoRtcpEP = new IPEndPoint(videoAddr, videoAnnounce.Port + 1);
                    }
                }
            }

            AdjustLocalTracks();

            // If we get to here then the remote description was compatible with the local media tracks.
            // Set the remote description and end points.
            RemoteDescription = sessionDescription;
            AudioDestinationEndPoint = remoteAudioRtpEP ?? AudioDestinationEndPoint;
            AudioControlDestinationEndPoint = remoteAudioRtcpEP ?? AudioControlDestinationEndPoint;
            VideoDestinationEndPoint = remoteVideoRtpEP ?? VideoDestinationEndPoint;
            VideoControlDestinationEndPoint = remoteVideoRtcpEP ?? VideoControlDestinationEndPoint;

            return SetDescriptionResultEnum.OK;
        }

        /// <summary>
        /// Sets the stream status on a local audio or video media track.
        /// </summary>
        /// <param name="kind">The type of the media track. Must be audio or video.</param>
        /// <param name="status">The stream status for the media track.</param>
        public void SetMediaStreamStatus(SDPMediaTypesEnum kind, MediaStreamStatusEnum status)
        {
            if(kind == SDPMediaTypesEnum.audio && AudioLocalTrack != null)
            {
                AudioLocalTrack.StreamStatus = status;
            }
            else if(kind == SDPMediaTypesEnum.video && VideoLocalTrack != null)
            {
                VideoLocalTrack.StreamStatus = status;
            }
        }

        /// <summary>
        /// Adjust the properties of the local media tracks based on the remote tracks. The remote party
        /// may not support both audio and video or may support different codecs. The local tracks need
        /// to be adjusted to ensure that the choice of what to send matches what the remote party is expecting.
        /// </summary>
        private void AdjustLocalTracks()
        {
            if (AudioLocalTrack != null && AudioRemoteTrack == null)
            {
                // The remote party does not support audio
                AudioLocalTrack.StreamStatus = MediaStreamStatusEnum.Inactive;
            }

            if (VideoLocalTrack != null && VideoRemoteTrack == null)
            {
                // The remote party does not support video.
                VideoLocalTrack.StreamStatus = MediaStreamStatusEnum.Inactive;
            }
        }

        /// <summary>
        /// Generates a session description from the provided list of tracks.
        /// </summary>
        /// <param name="tracks">The list of tracks to generate the session description for.</param>
        /// <param name="connectionAddress">Optional. If set this address will be used as 
        /// the SDP Connection address. If not specified the Internet facing address will
        /// be used.</param>
        /// <returns>A session description payload.</returns>
        private SDP GetSessionDesciption(List<MediaStreamTrack> tracks, IPAddress connectionAddress)
        {
            IPAddress localAddress = connectionAddress;

            if (localAddress == null)
            {
                if (AudioDestinationEndPoint?.Address != null)
                {
                    localAddress = NetServices.GetLocalAddressForRemote(AudioDestinationEndPoint.Address);
                }
                else if (VideoDestinationEndPoint?.Address != null)
                {
                    localAddress = NetServices.GetLocalAddressForRemote(VideoDestinationEndPoint.Address);
                }
                else
                {
                    localAddress = NetServices.InternetDefaultAddress;
                }
            }

            SDP sdp = new SDP(IPAddress.Loopback);
            sdp.SessionId = Crypto.GetRandomInt(5).ToString();

            sdp.Connection = new SDPConnectionInformation(localAddress);

            foreach (var track in tracks)
            {
                int rtpPort = 0; // A port of zero means the media type is not supported.
                if (track.Capabilties != null && track.Capabilties.Count() > 0)
                {
                    rtpPort = (m_isMediaMultiplexed) ? m_rtpChannels.Single().Value.RTPPort : m_rtpChannels[track.Kind].RTPPort;
                }

                SDPMediaAnnouncement announcement = new SDPMediaAnnouncement(
                   track.Kind,
                   rtpPort,
                   track.Capabilties);

                announcement.Transport = RTP_MEDIA_PROFILE;
                announcement.MediaStreamStatus = AudioLocalTrack.StreamStatus;

                sdp.Media.Add(announcement);
            }

            return sdp;
        }

        /// <summary>
        /// Gets the RTP channel being used to send and receive the specified media type for this session.
        /// If media multiplexing is being used there will only a single RTP channel.
        /// </summary>
        public RTPChannel GetRtpChannel(SDPMediaTypesEnum mediaType)
        {
            if (m_isMediaMultiplexed)
            {
                return m_rtpChannels.FirstOrDefault().Value;
            }
            else if (m_rtpChannels.ContainsKey(mediaType))
            {
                return m_rtpChannels[mediaType];
            }
            else
            {
                return null;
            }
        }

        /// <summary>
        /// Creates a new RTP channel (which manages the UDP socket sending and receiving RTP
        /// packets) for use with this session.
        /// </summary>
        /// <param name="mediaType">The type of media the RTP channel is for. Must be audio or video.</param>
        /// <returns>A new RTPChannel instance.</returns>
        private RTPChannel CreateRtpChannel(SDPMediaTypesEnum mediaType)
        {
            var rtpChannel = new RTPChannel(!m_isRtcpMultiplexed);
            m_rtpChannels.Add(mediaType, rtpChannel);

            rtpChannel.OnRTPDataReceived += OnReceive;
            rtpChannel.OnControlDataReceived += OnReceive; // RTCP packets could come on RTP or control socket.
            rtpChannel.OnClosed += OnRTPChannelClosed;

            // Start the RTP, and if required the Control, socket receivers and the RTCP session.
            rtpChannel.Start();

            return rtpChannel;
        }

        /// <summary>
        /// Creates a new RTCP session for a media track belonging to this RTP session.
        /// </summary>
        /// <param name="mediaType">The media type to create the RTP session for. Must be
        /// audio or video.</param>
        /// <returns>A new RTCPSession object. The RTCPSession must have its Start method called
        /// in order to commence sending RTCP reports.</returns>
        private RTCPSession CreateRtcpSession(SDPMediaTypesEnum mediaType)
        {
            var rtcpSession = new RTCPSession(mediaType, 0);
            rtcpSession.OnTimeout += (mt) => OnTimeout?.Invoke(mt);
            rtcpSession.OnReportReadyToSend += SendRtcpReport;

            return rtcpSession;
        }

        /// <summary>
        /// Sets the Secure RTP (SRTP) delegates and marks this session as ready for communications.
        /// </summary>
        /// <param name="protectRtp">SRTP encrypt RTP packet delegate.</param>
        /// <param name="unprotectRtp">SRTP decrypt RTP packet delegate.</param>
        /// <param name="protectRtcp">SRTP encrypt RTCP packet delegate.</param>
        /// <param name="unprotectRtcp">SRTP decrypt RTCP packet delegate.</param>
        public virtual void SetSecurityContext(
            ProtectRtpPacket protectRtp,
            ProtectRtpPacket unprotectRtp,
            ProtectRtpPacket protectRtcp,
            ProtectRtpPacket unprotectRtcp)
        {
            m_srtpProtect = protectRtp;
            m_srtpUnprotect = unprotectRtp;
            m_srtcpControlProtect = protectRtcp;
            m_srtcpControlUnprotect = unprotectRtcp;

            IsSecureContextReady = true;

            logger.LogDebug("Secure context successfully set on RTPSession.");
        }

        /// <summary>
        /// Sets the remote end points for a media type supported by this RTP session.
        /// </summary>
        /// <param name="mediaType">The media type, must be audio or video, to set the remote end point for.</param>
        /// <param name="rtpEndPoint">The remote end point for RTP packets corresponding to the media type.</param>
        /// <param name="rtcpEndPoint">The remote end point for RTCP packets corresponding to the media type.</param>
        public void SetDestination(SDPMediaTypesEnum mediaType, IPEndPoint rtpEndPoint, IPEndPoint rtcpEndPoint)
        {
            if (mediaType == SDPMediaTypesEnum.audio)
            {
                AudioDestinationEndPoint = rtpEndPoint;
                AudioControlDestinationEndPoint = rtcpEndPoint;
            }
            else if (mediaType == SDPMediaTypesEnum.video)
            {
                VideoDestinationEndPoint = rtpEndPoint;
                VideoControlDestinationEndPoint = rtcpEndPoint;
            }
        }

        /// <summary>
        /// Starts the RTCP session(s) that monitor this RTP session.
        /// </summary>
        public virtual Task Start()
        {
            if (!m_isStarted)
            {
                m_isStarted = true;

                if (HasAudio && m_audioRtcpSession != null && AudioLocalTrack.StreamStatus != MediaStreamStatusEnum.Inactive)
                {
                    // The local audio track may have been disabled if there were no matching capabilities with
                    // the remote party.
                    m_audioRtcpSession.Start();
                }

                if (HasVideo && m_videoRtcpSession != null && VideoLocalTrack.StreamStatus != MediaStreamStatusEnum.Inactive)
                {
                    // The local video track may have been disabled if there were no matching capabilities with
                    // the remote party.
                    m_videoRtcpSession.Start();
                }
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// Attempts to get the highest priority sending format for the remote call party/
        /// </summary>
        /// <param name="mediaType">The media type to get the sending format for.</param>
        /// <returns>The first compatible media format found for the specified media type.</returns>
        public SDPMediaFormat GetSendingFormat(SDPMediaTypesEnum mediaType)
        {
            if (mediaType == SDPMediaTypesEnum.audio)
            {
                if (AudioLocalTrack != null && AudioRemoteTrack != null)
                {
                    var format = SDPMediaFormat.GetCompatibleFormats(AudioLocalTrack.Capabilties, AudioRemoteTrack.Capabilties)
                        .Where(x => x.FormatID != RemoteRtpEventPayloadID.ToString()).FirstOrDefault();

                    if(format == null)
                    {
                        // It's not expected that this occurs as a compatibility check is done when the remote session description
                        // is set. By this point a compatible codec should be available.
                        throw new ApplicationException($"No compatible sending format could be found for media {mediaType}.");
                    }
                    else
                    {
                        return format;
                    }
                }
                else
                {
                    throw new ApplicationException($"Cannot get the {mediaType} sending format, missing either local or remote {mediaType} track.");
                }
            }
            else if (mediaType == SDPMediaTypesEnum.video)
            {
                if (VideoLocalTrack != null && VideoRemoteTrack != null)
                {
                    return SDPMediaFormat.GetCompatibleFormats(VideoLocalTrack.Capabilties, VideoRemoteTrack.Capabilties).First();
                }
                else
                {
                    throw new ApplicationException($"Cannot get the {mediaType} sending format, missing wither local or remote {mediaType} track.");
                }
            }
            else
            {
                throw new ApplicationException($"Sending of {mediaType} is not supported.");
            }
        }

        /// <summary>
        /// Sends an audio packet to the remote party.
        /// </summary>
        /// <param name="duration">The duration of the audio payload in timestamp units. This value
        /// gets added onto the timestamp being set in the RTP header.</param>
        /// <param name="payloadTypeID">The payload ID to set in the RTP header.</param>
        /// <param name="buffer">The audio payload to send.</param>
        public void SendAudioFrame(uint duration, int payloadTypeID, byte[] buffer)
        {
            if (m_isClosed || m_rtpEventInProgress || AudioDestinationEndPoint == null)
            {
                return;
            }

            try
            {
                var audioTrack = m_tracks.Where(x => x.Kind == SDPMediaTypesEnum.audio && !x.IsRemote).FirstOrDefault();

                if (audioTrack == null)
                {
                    logger.LogWarning("SendAudio was called on an RTP session without an audio stream.");
                }
                else
                {
                    for (int index = 0; index * RTP_MAX_PAYLOAD < buffer.Length; index++)
                    {
                        audioTrack.SeqNum = (ushort)(audioTrack.SeqNum % UInt16.MaxValue);

                        int offset = (index == 0) ? 0 : (index * RTP_MAX_PAYLOAD);
                        int payloadLength = (offset + RTP_MAX_PAYLOAD < buffer.Length) ? RTP_MAX_PAYLOAD : buffer.Length - offset;
                        byte[] payload = new byte[payloadLength];

                        Buffer.BlockCopy(buffer, offset, payload, 0, payloadLength);

                        // RFC3551 specifies that for audio the marker bit should always be 0 except for when returning
                        // from silence suppression. For video the marker bit DOES get set to 1 for the last packet
                        // in a frame.
                        int markerBit = 0;

                        var audioRtpChannel = GetRtpChannel(SDPMediaTypesEnum.audio);
                        SendRtpPacket(audioRtpChannel, AudioDestinationEndPoint, payload, audioTrack.Timestamp, markerBit, payloadTypeID, audioTrack.Ssrc, audioTrack.SeqNum++, m_audioRtcpSession);

                        //logger.LogDebug($"send audio { audioRtpChannel.RTPLocalEndPoint}->{AudioDestinationEndPoint}.");
                    }

                    audioTrack.Timestamp += duration;
                }
            }
            catch (SocketException sockExcp)
            {
                logger.LogError("SocketException SendAudioFrame. " + sockExcp.Message);
            }
        }

        /// <summary>
        /// Sends a VP8 frame as one or more RTP packets.
        /// </summary>
        /// <param name="timestamp">The timestamp to place in the RTP header. Needs
        /// to be based on a 90Khz clock.</param>
        /// <param name="payloadTypeID">The payload ID to place in the RTP header.</param>
        /// <param name="buffer">The VP8 encoded payload.</param>
        public void SendVp8Frame(uint duration, int payloadTypeID, byte[] buffer)
        {
            var dstEndPoint = m_isMediaMultiplexed ? AudioDestinationEndPoint : VideoDestinationEndPoint;

            if (m_isClosed || m_rtpEventInProgress || dstEndPoint == null)
            {
                return;
            }

            try
            {
                var videoTrack = m_tracks.Where(x => x.Kind == SDPMediaTypesEnum.video && !x.IsRemote).FirstOrDefault();

                if (videoTrack == null)
                {
                    logger.LogWarning("SendVp8Frame was called on an RTP session without a video stream.");
                }
                else
                {
                    for (int index = 0; index * RTP_MAX_PAYLOAD < buffer.Length; index++)
                    {
                        videoTrack.SeqNum = (ushort)(videoTrack.SeqNum % UInt16.MaxValue);

                        int offset = index * RTP_MAX_PAYLOAD;
                        int payloadLength = (offset + RTP_MAX_PAYLOAD < buffer.Length) ? RTP_MAX_PAYLOAD : buffer.Length - offset;

                        byte[] vp8HeaderBytes = (index == 0) ? new byte[] { 0x10 } : new byte[] { 0x00 };
                        byte[] payload = new byte[payloadLength + vp8HeaderBytes.Length];
                        Buffer.BlockCopy(vp8HeaderBytes, 0, payload, 0, vp8HeaderBytes.Length);
                        Buffer.BlockCopy(buffer, offset, payload, vp8HeaderBytes.Length, payloadLength);

                        int markerBit = ((offset + payloadLength) >= buffer.Length) ? 1 : 0; // Set marker bit for the last packet in the frame.

                        var videoChannel = GetRtpChannel(SDPMediaTypesEnum.video);
                        SendRtpPacket(videoChannel, dstEndPoint, payload, videoTrack.Timestamp, markerBit, payloadTypeID, videoTrack.Ssrc, videoTrack.SeqNum++, m_videoRtcpSession);

                        //logger.LogDebug($"send VP8 {videoChannel.RTPLocalEndPoint}->{dstEndPoint} timestamp {videoTrack.Timestamp}, sample length {buffer.Length}.");
                    }

                    videoTrack.Timestamp += duration;
                }
            }
            catch (SocketException sockExcp)
            {
                logger.LogError("SocketException SendVp8Frame. " + sockExcp.Message);
            }
        }

        /// <summary>
        /// Helper method to send a low quality JPEG image over RTP. This method supports a very abbreviated version of RFC 2435 "RTP Payload Format for JPEG-compressed Video".
        /// It's intended as a quick convenient way to send something like a test pattern image over an RTSP connection. More than likely it won't be suitable when a high
        /// quality image is required since the header used in this method does not support quantization tables.
        /// </summary>
        /// <param name="jpegBytes">The raw encoded bytes of the JPEG image to transmit.</param>
        /// <param name="jpegQuality">The encoder quality of the JPEG image.</param>
        /// <param name="jpegWidth">The width of the JPEG image.</param>
        /// <param name="jpegHeight">The height of the JPEG image.</param>
        /// <param name="framesPerSecond">The rate at which the JPEG frames are being transmitted at. used to calculate the timestamp.</param>
        public void SendJpegFrame(uint duration, int payloadTypeID, byte[] jpegBytes, int jpegQuality, int jpegWidth, int jpegHeight)
        {
            var dstEndPoint = m_isMediaMultiplexed ? AudioDestinationEndPoint : VideoDestinationEndPoint;

            if (m_isClosed || m_rtpEventInProgress || dstEndPoint == null)
            {
                return;
            }

            try
            {
                var videoTrack = m_tracks.Where(x => x.Kind == SDPMediaTypesEnum.video && !x.IsRemote).FirstOrDefault();

                if (videoTrack == null)
                {
                    logger.LogWarning("SendJpegFrame was called on an RTP session without a video stream.");
                }
                else
                {
                    for (int index = 0; index * RTP_MAX_PAYLOAD < jpegBytes.Length; index++)
                    {
                        uint offset = Convert.ToUInt32(index * RTP_MAX_PAYLOAD);
                        int payloadLength = ((index + 1) * RTP_MAX_PAYLOAD < jpegBytes.Length) ? RTP_MAX_PAYLOAD : jpegBytes.Length - index * RTP_MAX_PAYLOAD;
                        byte[] jpegHeader = CreateLowQualityRtpJpegHeader(offset, jpegQuality, jpegWidth, jpegHeight);

                        List<byte> packetPayload = new List<byte>();
                        packetPayload.AddRange(jpegHeader);
                        packetPayload.AddRange(jpegBytes.Skip(index * RTP_MAX_PAYLOAD).Take(payloadLength));

                        int markerBit = ((index + 1) * RTP_MAX_PAYLOAD < jpegBytes.Length) ? 0 : 1; ;
                        SendRtpPacket(GetRtpChannel(SDPMediaTypesEnum.video), dstEndPoint, packetPayload.ToArray(), videoTrack.Timestamp, markerBit, payloadTypeID, videoTrack.Ssrc, videoTrack.SeqNum++, m_videoRtcpSession);
                    }

                    videoTrack.Timestamp += duration;
                }
            }
            catch (SocketException sockExcp)
            {
                logger.LogError("SocketException SendJpegFrame. " + sockExcp.Message);
            }
        }

        /// <summary>
        /// H264 frames need a two byte header when transmitted over RTP.
        /// </summary>
        /// <param name="frame">The H264 encoded frame to transmit.</param>
        /// <param name="frameSpacing">The increment to add to the RTP timestamp for each new frame.</param>
        /// <param name="payloadType">The payload type to set on the RTP packet.</param>
        public void SendH264Frame(uint duration, int payloadTypeID, byte[] frame)
        {
            var dstEndPoint = m_isMediaMultiplexed ? AudioDestinationEndPoint : VideoDestinationEndPoint;

            if (m_isClosed || m_rtpEventInProgress || dstEndPoint == null)
            {
                return;
            }

            try
            {
                var videoTrack = m_tracks.Where(x => x.Kind == SDPMediaTypesEnum.video && !x.IsRemote).FirstOrDefault();

                if (videoTrack == null)
                {
                    logger.LogWarning("SendH264Frame was called on an RTP session without a video stream.");
                }
                else
                {
                    for (int index = 0; index * RTP_MAX_PAYLOAD < frame.Length; index++)
                    {
                        int offset = index * RTP_MAX_PAYLOAD;
                        int payloadLength = ((index + 1) * RTP_MAX_PAYLOAD < frame.Length) ? RTP_MAX_PAYLOAD : frame.Length - index * RTP_MAX_PAYLOAD;
                        byte[] payload = new byte[payloadLength + H264_RTP_HEADER_LENGTH];

                        // Start RTP packet in frame 0x1c 0x89
                        // Middle RTP packet in frame 0x1c 0x09
                        // Last RTP packet in frame 0x1c 0x49

                        int markerBit = 0;
                        byte[] h264Header = new byte[] { 0x1c, 0x09 };

                        if (index == 0 && frame.Length < RTP_MAX_PAYLOAD)
                        {
                            // First and last RTP packet in the frame.
                            h264Header = new byte[] { 0x1c, 0x49 };
                            markerBit = 1;
                        }
                        else if (index == 0)
                        {
                            h264Header = new byte[] { 0x1c, 0x89 };
                        }
                        else if ((index + 1) * RTP_MAX_PAYLOAD > frame.Length)
                        {
                            h264Header = new byte[] { 0x1c, 0x49 };
                            markerBit = 1;
                        }

                        var h264Stream = frame.Skip(index * RTP_MAX_PAYLOAD).Take(payloadLength).ToList();
                        h264Stream.InsertRange(0, h264Header);

                        Buffer.BlockCopy(h264Header, 0, payload, 0, H264_RTP_HEADER_LENGTH);
                        Buffer.BlockCopy(frame, offset, payload, H264_RTP_HEADER_LENGTH, payloadLength);

                        SendRtpPacket(GetRtpChannel(SDPMediaTypesEnum.video), dstEndPoint, payload, videoTrack.Timestamp, markerBit, payloadTypeID, videoTrack.Ssrc, videoTrack.SeqNum++, m_videoRtcpSession);
                    }

                    videoTrack.Timestamp += duration;
                }
            }
            catch (SocketException sockExcp)
            {
                logger.LogError("SocketException SendH264Frame. " + sockExcp.Message);
            }
        }

        /// <summary>
        /// Sends an RTP event for a DTMF tone as per RFC2833. Sending the event requires multiple packets to be sent.
        /// This method will hold onto the socket until all the packets required for the event have been sent. The send
        /// can be cancelled using the cancellation token.
        /// </summary>
        /// <param name="rtpEvent">The RTP event to send.</param>
        /// <param name="cancellationToken">CancellationToken to allow the operation to be cancelled prematurely.</param>
        /// <param name="clockRate">To send an RTP event the clock rate of the underlying stream needs to be known.</param>
        /// <param name="streamID">For multiplexed sessions the ID of the stream to send the event on. Defaults to 0
        /// for single stream sessions.</param>
        public async Task SendDtmfEvent(
            RTPEvent rtpEvent,
            CancellationToken cancellationToken,
            int clockRate = DEFAULT_AUDIO_CLOCK_RATE)
        {
            var dstEndPoint = AudioDestinationEndPoint;

            if (m_isClosed || m_rtpEventInProgress == true || dstEndPoint == null)
            {
                logger.LogWarning("SendDtmfEvent request ignored as an RTP event is already in progress.");
            }

            try
            {
                var audioTrack = m_tracks.Where(x => x.Kind == SDPMediaTypesEnum.audio && !x.IsRemote).FirstOrDefault();

                if (audioTrack == null)
                {
                    logger.LogWarning("SendDtmfEvent was called on an RTP session without an audio stream.");
                }
                else
                {
                    m_rtpEventInProgress = true;
                    uint startTimestamp = m_lastRtpTimestamp;

                    // The sample period in milliseconds being used for the media stream that the event 
                    // is being inserted into. Should be set to 50ms if main media stream is dynamic or 
                    // sample period is unknown.
                    int samplePeriod = RTP_EVENT_DEFAULT_SAMPLE_PERIOD_MS;

                    // The RTP timestamp step corresponding to the sampling period. This can change depending
                    // on the codec being used. For example using PCMU with a sampling frequency of 8000Hz and a sample period of 50ms
                    // the timestamp step is 400 (8000 / (1000 / 50)). For a sample period of 20ms it's 160 (8000 / (1000 / 20)).
                    ushort rtpTimestampStep = (ushort)(clockRate * samplePeriod / 1000);

                    // If only the minimum number of packets are being sent then they are both the start and end of the event.
                    rtpEvent.EndOfEvent = (rtpEvent.TotalDuration <= rtpTimestampStep);
                    // The DTMF tone is generally multiple RTP events. Each event has a duration of the RTP timestamp step.
                    rtpEvent.Duration = rtpTimestampStep;

                    // Send the start of event packets.
                    for (int i = 0; i < RTPEvent.DUPLICATE_COUNT && !cancellationToken.IsCancellationRequested; i++)
                    {
                        byte[] buffer = rtpEvent.GetEventPayload();

                        int markerBit = (i == 0) ? 1 : 0;  // Set marker bit for the first packet in the event.
                        SendRtpPacket(GetRtpChannel(SDPMediaTypesEnum.audio), dstEndPoint, buffer, startTimestamp, markerBit, rtpEvent.PayloadTypeID, audioTrack.Ssrc, audioTrack.SeqNum, m_audioRtcpSession);

                        audioTrack.SeqNum++;
                    }

                    await Task.Delay(samplePeriod, cancellationToken).ConfigureAwait(false);

                    if (!rtpEvent.EndOfEvent)
                    {
                        // Send the progressive event packets 
                        while ((rtpEvent.Duration + rtpTimestampStep) < rtpEvent.TotalDuration && !cancellationToken.IsCancellationRequested)
                        {
                            rtpEvent.Duration += rtpTimestampStep;
                            byte[] buffer = rtpEvent.GetEventPayload();

                            SendRtpPacket(GetRtpChannel(SDPMediaTypesEnum.audio), dstEndPoint, buffer, startTimestamp, 0, rtpEvent.PayloadTypeID, audioTrack.Ssrc, audioTrack.SeqNum, m_audioRtcpSession);

                            audioTrack.SeqNum++;

                            await Task.Delay(samplePeriod, cancellationToken).ConfigureAwait(false);
                        }

                        // Send the end of event packets.
                        for (int j = 0; j < RTPEvent.DUPLICATE_COUNT && !cancellationToken.IsCancellationRequested; j++)
                        {
                            rtpEvent.EndOfEvent = true;
                            rtpEvent.Duration = rtpEvent.TotalDuration;
                            byte[] buffer = rtpEvent.GetEventPayload();

                            SendRtpPacket(GetRtpChannel(SDPMediaTypesEnum.audio), dstEndPoint, buffer, startTimestamp, 0, rtpEvent.PayloadTypeID, audioTrack.Ssrc, audioTrack.SeqNum, m_audioRtcpSession);

                            audioTrack.SeqNum++;
                        }
                    }
                }
            }
            catch (SocketException sockExcp)
            {
                logger.LogError("SocketException SendDtmfEvent. " + sockExcp.Message);
            }
            catch (TaskCanceledException)
            {
                logger.LogWarning("SendDtmfEvent was cancelled by caller.");
            }
            finally
            {
                m_rtpEventInProgress = false;
            }
        }

        /// <summary>
        /// Close the session and RTP channel.
        /// </summary>
        public void CloseSession(string reason)
        {
            if (!m_isClosed)
            {
                m_isClosed = true;

                m_audioRtcpSession?.Close(reason);
                m_videoRtcpSession?.Close(reason);

                foreach (var rtpChannel in m_rtpChannels.Values)
                {
                    rtpChannel.OnRTPDataReceived -= OnReceive;
                    rtpChannel.OnControlDataReceived -= OnReceive;
                    rtpChannel.OnClosed -= OnRTPChannelClosed;
                    rtpChannel.Close(reason);
                }

                OnRtpClosed?.Invoke(reason);
            }
        }

        /// <summary>
        /// Event handler for receiving data on the RTP and Control channels. For multiplexed
        /// sessions both RTP and RTCP packets will be received on the RTP channel.
        /// </summary>
        /// <param name="localEndPoint">The local end point the data was received on.</param>
        /// <param name="remoteEndPoint">The remote end point the data was received from.</param>
        /// <param name="buffer">The data received.</param>
        private void OnReceive(IPEndPoint localEndPoint, IPEndPoint remoteEndPoint, byte[] buffer)
        {
            // Quick sanity check on whether this is not an RTP or RTCP packet.
            if (buffer?.Length > RTPHeader.MIN_HEADER_LEN && buffer[0] >= 128 && buffer[0] <= 191)
            {
                if (IsSecure && !IsSecureContextReady)
                {
                    logger.LogWarning("RTP or RTCP packet received before secure context ready.");
                }
                else if (buffer[1] == 0xC8 /* RTCP SR */ || buffer[1] == 0xC9 /* RTCP RR */)
                {
                    //logger.LogDebug($"RTCP packet received from {remoteEndPoint} {buffer.HexStr()}");

                    #region RTCP packet.

                    if (m_srtcpControlUnprotect != null)
                    {
                        int outBufLen = 0;
                        int res = m_srtcpControlUnprotect(buffer, buffer.Length, out outBufLen);

                        if (res != 0)
                        {
                            logger.LogWarning($"SRTCP unprotect failed, result {res}.");
                            return;
                        }
                        else
                        {
                            buffer = buffer.Take(outBufLen).ToArray();
                        }
                    }

                    var rtcpPkt = new RTCPCompoundPacket(buffer);

                    if (rtcpPkt != null)
                    {
                        if (rtcpPkt.Bye != null)
                        {
                            logger.LogDebug($"RTCP BYE received for SSRC {rtcpPkt.Bye.SSRC}, reason {rtcpPkt.Bye.Reason}.");

                            OnRtcpBye?.Invoke(rtcpPkt.Bye.Reason);
                        }
                        else if (!m_isClosed)
                        {
                            var rtcpSession = GetRtcpSession(rtcpPkt);
                            if (rtcpSession != null)
                            {
                                if (rtcpSession.LastActivityAt == DateTime.MinValue)
                                {
                                    // On the first received RTCP report for a session check whether the remote end point matches the
                                    // expected remote end point. If not it's "likely" that a private IP address was specified in the SDP.
                                    // Take the risk and switch the remote control end point to the one we are receiving from.

                                    if (rtcpSession == m_audioRtcpSession && AudioControlDestinationEndPoint != remoteEndPoint)
                                    {
                                        logger.LogDebug($"Audio control end point switched to {remoteEndPoint}.");
                                        AudioControlDestinationEndPoint = remoteEndPoint;
                                    }
                                    else if (rtcpSession == m_videoRtcpSession && VideoControlDestinationEndPoint != remoteEndPoint)
                                    {
                                        logger.LogDebug($"Video control end point switched to {remoteEndPoint}.");
                                        VideoControlDestinationEndPoint = remoteEndPoint;
                                    }
                                }

                                rtcpSession.ReportReceived(remoteEndPoint, rtcpPkt);
                                OnReceiveReport?.Invoke(rtcpSession.MediaType, rtcpPkt);
                            }
                            else
                            {
                                logger.LogWarning("Could not match an RTCP packet against any SSRC's in the session.");
                            }
                        }
                    }
                    else
                    {
                        logger.LogWarning("Failed to parse RTCP compound report.");
                    }

                    #endregion
                }
                else
                {
                    #region RTP packet.

                    if (!m_isClosed)
                    {
                        if (m_srtpUnprotect != null)
                        {
                            int outBufLen = 0;
                            int res = m_srtpUnprotect(buffer, buffer.Length, out outBufLen);

                            if (res != 0)
                            {
                                logger.LogWarning($"SRTP unprotect failed, result {res}.");
                                return;
                            }
                            else
                            {
                                buffer = buffer.Take(outBufLen).ToArray();
                            }
                        }

                        var rtpPacket = new RTPPacket(buffer);

                        SDPMediaTypesEnum? rtpMediaType = null;

                        // Check whether this is an RTP event.
                        if (RemoteRtpEventPayloadID != 0 && rtpPacket.Header.PayloadType == RemoteRtpEventPayloadID)
                        {
                            RTPEvent rtpEvent = new RTPEvent(rtpPacket.Payload);
                            OnRtpEvent?.Invoke(rtpEvent, rtpPacket.Header);
                        }
                        else
                        {
                            // Attempt to determine the media type for the RTP packet.
                            if (m_isMediaMultiplexed)
                            {
                                rtpMediaType = GetMediaTypeForRtpPacket(rtpPacket.Header);
                            }
                            else
                            {
                                rtpMediaType = GetMediaTypeForLocalEndPoint(localEndPoint);
                            }

                            // Set the remote track SSRC so that RTCP reports can match the media type.
                            if (rtpMediaType == SDPMediaTypesEnum.audio && AudioRemoteTrack?.Ssrc == 0)
                            {
                                logger.LogDebug($"Set remote audio track SSRC to {rtpPacket.Header.SyncSource}.");
                                AudioRemoteTrack.Ssrc = rtpPacket.Header.SyncSource;

                                if(AudioDestinationEndPoint != remoteEndPoint)
                                {
                                    logger.LogDebug($"Audio end point switched to {remoteEndPoint}.");

                                    // If the remote end point doesn't match where we are sending then it's likely a private IP address
                                    // was specified in the SDP. We take the risk that the first packet came from the genuine source and
                                    // switch the RTP audio end point.
                                    AudioDestinationEndPoint = remoteEndPoint;
                                }
                            }
                            else if (rtpMediaType == SDPMediaTypesEnum.video && VideoRemoteTrack?.Ssrc == 0)
                            {
                                logger.LogDebug($"Set remote video track SSRC to {rtpPacket.Header.SyncSource}.");
                                VideoRemoteTrack.Ssrc = rtpPacket.Header.SyncSource;

                                if (VideoDestinationEndPoint != remoteEndPoint)
                                {
                                    logger.LogDebug($"Video end point switched to {remoteEndPoint}.");

                                    // If the remote end point doesn't match where we are sending then it's likely a private IP address
                                    // was specified in the SDP. We take the risk that the first packet came from the genuine source and
                                    // switch the RTP video end point.
                                    VideoDestinationEndPoint = remoteEndPoint;
                                }
                            }

                            SDPMediaTypesEnum mediaType = (rtpMediaType.HasValue) ? rtpMediaType.Value : DEFAULT_MEDIA_TYPE;
                            OnRtpPacketReceived?.Invoke(mediaType, rtpPacket);

                            // Used for reporting purposes.
                            if (rtpMediaType == SDPMediaTypesEnum.audio && m_audioRtcpSession != null)
                            {
                                m_audioRtcpSession.RecordRtpPacketReceived(rtpPacket);
                            }
                            else if (rtpMediaType == SDPMediaTypesEnum.video && m_videoRtcpSession != null)
                            {
                                m_videoRtcpSession.RecordRtpPacketReceived(rtpPacket);
                            }
                        }
                    }

                    #endregion
                }
            }
        }

        /// <summary>
        /// Attempts to determine which media stream a received RTP packet is for based on the RTP socket
        /// it was received on. This is for cases where media multiplexing is not in use (i.e. legacy RTP).
        /// </summary>
        /// <param name="localEndPoint">The local end point the RTP packet was received on.</param>
        /// <returns>The media type for the received packet or null if it could not be determined.</returns>
        private SDPMediaTypesEnum? GetMediaTypeForLocalEndPoint(IPEndPoint localEndPoint)
        {
            if (m_rtpChannels.ContainsKey(SDPMediaTypesEnum.audio) && m_rtpChannels[SDPMediaTypesEnum.audio].RTPPort == localEndPoint.Port)
            {
                return SDPMediaTypesEnum.audio;
            }
            else if (m_rtpChannels.ContainsKey(SDPMediaTypesEnum.video) && m_rtpChannels[SDPMediaTypesEnum.video].RTPPort == localEndPoint.Port)
            {
                return SDPMediaTypesEnum.video;
            }
            else
            {
                return null;
            }
        }

        /// <summary>
        /// Attempts to determine which media stream a received RTP packet is for.
        /// </summary>
        /// <param name="header">The header of the received RTP packet.</param>
        /// <returns>The media type for the received packet or null if it could not be determined.</returns>
        private SDPMediaTypesEnum? GetMediaTypeForRtpPacket(RTPHeader header)
        {
            if (AudioRemoteTrack != null && AudioRemoteTrack.Ssrc == header.SyncSource)
            {
                return SDPMediaTypesEnum.audio;
            }
            else if (VideoRemoteTrack != null && VideoRemoteTrack.Ssrc == header.SyncSource)
            {
                return SDPMediaTypesEnum.video;
            }
            else if (AudioRemoteTrack != null && AudioRemoteTrack.IsPayloadIDMatch(header.PayloadType))
            {
                return SDPMediaTypesEnum.audio;
            }
            else if (VideoRemoteTrack != null && VideoRemoteTrack.IsPayloadIDMatch(header.PayloadType))
            {
                return SDPMediaTypesEnum.video;
            }

            logger.LogWarning($"An RTP packet with payload ID {header.PayloadType} was received that could not be matched to an audio or video stream.");
            return null;
        }

        /// <summary>
        /// Attempts to get the RTCP session that matches a received RTCP report.
        /// </summary>
        /// <param name="rtcpPkt">The RTCP compound packet received from the remote party.</param>
        /// <returns>If a match could be found an SSRC the RTCP session otherwise null.</returns>
        private RTCPSession GetRtcpSession(RTCPCompoundPacket rtcpPkt)
        {
            if (rtcpPkt.SenderReport != null)
            {
                if (AudioRemoteTrack != null && rtcpPkt.SenderReport.SSRC == AudioRemoteTrack.Ssrc)
                {
                    return m_audioRtcpSession;
                }
                else if (VideoRemoteTrack != null && rtcpPkt.SenderReport.SSRC == VideoRemoteTrack.Ssrc)
                {
                    return m_videoRtcpSession;
                }
            }
            else if (rtcpPkt.ReceiverReport != null)
            {
                if (AudioRemoteTrack != null && rtcpPkt.ReceiverReport.SSRC == AudioRemoteTrack.Ssrc)
                {
                    return m_audioRtcpSession;
                }
                else if (VideoRemoteTrack != null && rtcpPkt.ReceiverReport.SSRC == VideoRemoteTrack.Ssrc)
                {
                    return m_videoRtcpSession;
                }
            }

            // No match on SR/RR SSRC. Check the individual reception reports for a known SSRC.
            List<ReceptionReportSample> receptionReports = null;

            if (rtcpPkt.SenderReport != null)
            {
                receptionReports = rtcpPkt.SenderReport.ReceptionReports;
            }
            else if (rtcpPkt.ReceiverReport != null)
            {
                receptionReports = rtcpPkt.ReceiverReport.ReceptionReports;
            }

            if (receptionReports != null && receptionReports.Count > 0)
            {
                foreach (var recRep in receptionReports)
                {
                    if (AudioLocalTrack != null && recRep.SSRC == AudioLocalTrack.Ssrc)
                    {
                        return m_audioRtcpSession;
                    }
                    else if (VideoLocalTrack != null && recRep.SSRC == VideoLocalTrack.Ssrc)
                    {
                        return m_videoRtcpSession;
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Does the actual sending of an RTP packet using the specified data and header values.
        /// </summary>
        /// <param name="rtpChannel">The RTP channel to send from.</param>
        /// <param name="dstRtpSocket">Destination to send to.</param>
        /// <param name="data">The RTP packet payload.</param>
        /// <param name="timestamp">The RTP header timestamp.</param>
        /// <param name="markerBit">The RTP header marker bit.</param>
        /// <param name="payloadType">The RTP header payload type.</param>
        private void SendRtpPacket(RTPChannel rtpChannel, IPEndPoint dstRtpSocket, byte[] data, uint timestamp, int markerBit, int payloadType, uint ssrc, ushort seqNum, RTCPSession rtcpSession)
        {
            if (IsSecure && !IsSecureContextReady)
            {
                logger.LogWarning("SendRtpPacket cannot be called on a secure session before calling SetSecurityContext.");
            }
            else
            {
                int srtpProtectionLength = (m_srtpProtect != null) ? SRTP_MAX_PREFIX_LENGTH : 0;

                RTPPacket rtpPacket = new RTPPacket(data.Length + srtpProtectionLength);
                rtpPacket.Header.SyncSource = ssrc;
                rtpPacket.Header.SequenceNumber = seqNum;
                rtpPacket.Header.Timestamp = timestamp;
                rtpPacket.Header.MarkerBit = markerBit;
                rtpPacket.Header.PayloadType = payloadType;

                Buffer.BlockCopy(data, 0, rtpPacket.Payload, 0, data.Length);

                var rtpBuffer = rtpPacket.GetBytes();

                if (m_srtpProtect == null)
                {
                    rtpChannel.SendAsync(RTPChannelSocketsEnum.RTP, dstRtpSocket, rtpBuffer);
                }
                else
                {
                    int outBufLen = 0;
                    int rtperr = m_srtpProtect(rtpBuffer, rtpBuffer.Length - srtpProtectionLength, out outBufLen);
                    if (rtperr != 0)
                    {
                        logger.LogError("SendRTPPacket protection failed, result " + rtperr + ".");
                    }
                    else
                    {
                        rtpChannel.SendAsync(RTPChannelSocketsEnum.RTP, dstRtpSocket, rtpBuffer.Take(outBufLen).ToArray());
                    }
                }
                m_lastRtpTimestamp = timestamp;

                rtcpSession?.RecordRtpPacketSend(rtpPacket);
            }
        }

        /// <summary>
        /// Sends the RTCP report to the remote call party.
        /// </summary>
        /// <param name="report">RTCP report to send.</param>
        private void SendRtcpReport(SDPMediaTypesEnum mediaType, RTCPCompoundPacket report)
        {
            IPEndPoint controlDstEndPoint = null;
            if (m_isMediaMultiplexed || mediaType == SDPMediaTypesEnum.audio)
            {
                controlDstEndPoint = AudioControlDestinationEndPoint;
            }
            else if (mediaType == SDPMediaTypesEnum.video)
            {
                controlDstEndPoint = VideoControlDestinationEndPoint;
            }

            if (IsSecure && !IsSecureContextReady)
            {
                logger.LogWarning("SendRtcpReport cannot be called on a secure session before calling SetSecurityContext.");
            }
            else if (controlDstEndPoint != null)
            {
                var reportBytes = report.GetBytes();

                //logger.LogDebug($"SendRtcpReport: {reportBytes.HexStr()}");

                var sendOnSocket = (m_isRtcpMultiplexed) ? RTPChannelSocketsEnum.RTP : RTPChannelSocketsEnum.Control;

                var rtpChannel = GetRtpChannel(mediaType);

                if (m_srtcpControlProtect == null)
                {
                    rtpChannel.SendAsync(sendOnSocket, controlDstEndPoint, reportBytes);
                }
                else
                {
                    byte[] sendBuffer = new byte[reportBytes.Length + SRTP_MAX_PREFIX_LENGTH];
                    Buffer.BlockCopy(reportBytes, 0, sendBuffer, 0, reportBytes.Length);

                    int outBufLen = 0;
                    int rtperr = m_srtcpControlProtect(sendBuffer, sendBuffer.Length - SRTP_MAX_PREFIX_LENGTH, out outBufLen);
                    if (rtperr != 0)
                    {
                        logger.LogWarning("SRTP RTCP packet protection failed, result " + rtperr + ".");
                    }
                    else
                    {
                        rtpChannel.SendAsync(sendOnSocket, controlDstEndPoint, sendBuffer.Take(outBufLen).ToArray());
                    }
                }

                OnSendReport?.Invoke(mediaType, report);
            }
        }

        /// <summary>
        /// Utility function to create RtpJpegHeader either for initial packet or template for further packets
        /// 
        /// <code>
        /// 0                   1                   2                   3
        /// 0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1
        /// +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
        /// | Type-specific |              Fragment Offset                  |
        /// +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
        /// |      Type     |       Q       |     Width     |     Height    |
        /// +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
        /// </code>
        /// </summary>
        /// <param name="fragmentOffset"></param>
        /// <param name="quality"></param>
        /// <param name="width"></param>
        /// <param name="height"></param>
        /// <returns></returns>
        private static byte[] CreateLowQualityRtpJpegHeader(uint fragmentOffset, int quality, int width, int height)
        {
            byte[] rtpJpegHeader = new byte[8] { 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00 };

            // Byte 0: Type specific
            //http://tools.ietf.org/search/rfc2435#section-3.1.1

            // Bytes 1 to 3: Three byte fragment offset
            //http://tools.ietf.org/search/rfc2435#section-3.1.2

            if (BitConverter.IsLittleEndian)
            {
                fragmentOffset = NetConvert.DoReverseEndian(fragmentOffset);
            }

            byte[] offsetBytes = BitConverter.GetBytes(fragmentOffset);
            rtpJpegHeader[1] = offsetBytes[2];
            rtpJpegHeader[2] = offsetBytes[1];
            rtpJpegHeader[3] = offsetBytes[0];

            // Byte 4: JPEG Type.
            //http://tools.ietf.org/search/rfc2435#section-3.1.3

            //Byte 5: http://tools.ietf.org/search/rfc2435#section-3.1.4 (Q)
            rtpJpegHeader[5] = (byte)quality;

            // Byte 6: http://tools.ietf.org/search/rfc2435#section-3.1.5 (Width)
            rtpJpegHeader[6] = (byte)(width / 8);

            // Byte 7: http://tools.ietf.org/search/rfc2435#section-3.1.6 (Height)
            rtpJpegHeader[7] = (byte)(height / 8);

            return rtpJpegHeader;
        }

        /// <summary>
        /// Event handler for the RTP channel closure.
        /// </summary>
        private void OnRTPChannelClosed(string reason)
        {
            CloseSession(reason);
        }

        protected virtual void Dispose(bool disposing)
        {
            CloseSession(null);
        }

        public void Dispose()
        {
            CloseSession(null);
        }
    }
}