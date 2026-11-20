# Multi-arch image (amd64 + arm64). Built on the build machine's own platform, cross-published for the target.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src
COPY src/JellyLite/JellyLite.csproj src/JellyLite/
RUN RID=linux-musl-$([ "$TARGETARCH" = "arm64" ] && echo arm64 || echo x64) && \
    dotnet restore src/JellyLite/JellyLite.csproj -r $RID
COPY src/ src/
RUN RID=linux-musl-$([ "$TARGETARCH" = "arm64" ] && echo arm64 || echo x64) && \
    dotnet publish src/JellyLite/JellyLite.csproj -c Release -r $RID --self-contained false --no-restore -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine
# ffprobe reads tracks/chapters, ffmpeg extracts embedded text subtitles (never used for transcoding)
RUN apk add --no-cache ffmpeg
WORKDIR /app
COPY --from=build /app .
ENV JELLYLITE_DATA=/data \
    ASPNETCORE_HTTP_PORTS= \
    DOTNET_gcServer=0
VOLUME /data
EXPOSE 8096
ENTRYPOINT ["dotnet", "JellyLite.dll"]
