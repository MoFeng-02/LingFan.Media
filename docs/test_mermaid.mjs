import mermaid from 'mermaid';

const graphs = [
`flowchart TD
    A["IMediaSource\\nFile · Network · Stream"] -->|"MediaStreamFactory.CreateAsync"| B["IMediaStream\\nFile · Network · PassThrough"]
    B -->|"DemuxerFactory.Create"| C["IDemuxer — switchable backends\\nFFmpeg (primary) · MediaFoundation · LibVLC"]
    C --> D["Decoders → FrameChannel (IFrameChannel) → Sinks\\nVideoView · AudioOutput · CV pipeline"]
    style C stroke:#3b82f6,stroke-width:2px`,

`sequenceDiagram
    autonumber
    participant Caller
    participant Player as MediaPlayer
    participant Factory as streamFactory
    participant Demux as Demuxer
    participant Session as MediaSession
    Caller->>Player: OpenAsync(IMediaSource)
    Player->>Factory: CreateAsync(source)
    Note over Factory: Network → DNS + SSRF guard
    Factory-->>Player: IMediaStream
    Player->>Demux: demuxerFactory.Create(stream) → OpenAsync(stream)
    Demux-->>Session: tracks, metadata, duration, isLive
    Session-->>Player: ready (decoders / renderer / audio initialized)
    Player-->>Caller: Ready → Play() / Pause() / Seek()`
];

for (const g of graphs) {
  try {
    await mermaid.render('id', g);
    console.log('OK');
  } catch (e) {
    console.log('ERROR:', e.message);
  }
}
