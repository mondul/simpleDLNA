namespace NMaier.SimpleDlna.Server
{
  public enum DlnaMime
  {
    AudioAAC,
    AudioFLAC,
    AudioMP2,
    AudioMP3,
    AudioRAW,
    AudioVORBIS,
    ImageGIF,
    ImageJPEG,
    ImagePNG,
    SubtitleSRT,
    Video3GPP,
    VideoAVC,
    VideoAVI,
    VideoFLV,
    VideoMKV,
    VideoMPEG,
    VideoOGV,
    VideoWMV,

    // Appended rather than kept alphabetical, so the numeric values of the
    // existing members do not change.
    VideoWEBM
  }
}
