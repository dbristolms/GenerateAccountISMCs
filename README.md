# GenerateAccountISMCs
**This application generates ISMC files for each of your streaming assets if none exist.**

This application was written in C#.NET.  It uses the Azure Media Services [v3 API](https://docs.microsoft.com/en-us/azure/media-services/latest/media-services-apis-overview).

When using Azure Media Services to deliver video through a streaming endpoint the video is [dynamically packaged](https://docs.microsoft.com/en-us/azure/media-services/latest/dynamic-packaging-overview) into whatever streaming protocol is requested (HLS, MPEG-DASH, Smooth Streaming).  If you choose not to encode your video in Azure Media Services and instead encode the video with some other encoder you will need to create an ISM file to be able to stream.  The ISM is a [SMIL](https://docs.microsoft.com/en-us/iis/extensions/smooth-streaming-manifest-structure/iis-smooth-streaming-server-manifest-on-demand-smil-element) based XML file that effectively maps all of the different bitrate renditions of the video to the file names of those different bitrates.  This allows clients to make RESTful requests for different bitrates of video.

Another manifest file is the ISMC.  This is considered the client manifest.  This is what is delivered to the client when it requests the manifest URL (yourvideo.ism/manifest) even though the client request the ISM.  If no ISMC file exists the Media Services Streaming Endpoint will have to create one at the time of the client request.  The Streaming Endpoint only stores this created ISMC in memory.  It does not write it back to the asset.  To create the ISMC the streaming endpoint must read all of the MP4 files listed in the ISM.  This is an expensive read operation.  Additionally multiple different virtual machines can be used to back a single Streaming Endpoint.  Finally eventually the ISMC will be removed from memory if there are not reqular requests for the manifest.  Due to these factors, not having an ISMC can create extra load on the Streaming Endpoint.  This application was created to write a perminent ISMC to the asset so that customer encoded content created without an ISMC will not impact Streaming Endpoint performance.

This application uses both the Azure Storage API as well as the Media Services v3 API.  The workflow is basically:
1) Start the streaming endpoint if it is not running.
2) Setup a loop to go through all of the published streaming locators.
3) Check to see if a .ismc file exists.
4) If not, request the Smooth Streaming manifest for this asset.
5) Parse the resulting manifest and save it back to the asset.
6) Update the ISM with the ISMC filename.
