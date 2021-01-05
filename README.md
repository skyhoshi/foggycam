# 📹 FoggyCam - Nest Camera Recorder

A tool to locally capture your own Nest camera stream. You can read more about my approach in the [recently published blog post](https://den.dev/blog/free-nest-video-recording/). This approach supersedes the previous implementation that relied on the `/get_image` API and instead captures the H.264 stream directly through the Nest WebSocket service.