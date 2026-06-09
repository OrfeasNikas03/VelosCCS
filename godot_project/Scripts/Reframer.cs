using System;
using System.Collections.Generic;
using System.IO;
using OpenCvSharp;

namespace VelosCCS;

public class Reframer
{
    private readonly string _method;

    public Reframer(string method = "center")
    {
        _method = method;
    }

    public (int x, int y, int w, int h) GetCropRect(
        string videoPath, double start, double duration)
    {
        Log.Print($"[Reframe] GetCropRect entry: method={_method}, path={videoPath}, start={start:F2}, duration={duration:F2}");
        var result = _method switch
        {
            "face" => FaceTrackCrop(videoPath, start, duration),
            _ => CenterCrop(videoPath),
        };
        Log.Print($"[Reframe] GetCropRect result: ({result.x},{result.y},{result.w},{result.h})");
        return result;
    }

    public static (int x, int y, int w, int h) CenterCrop(string videoPath)
    {
        Log.Print($"[Reframe] CenterCrop entry: {videoPath}");
        using var cap = new VideoCapture(videoPath);
        int width = (int)cap.Get(VideoCaptureProperties.FrameWidth);
        int height = (int)cap.Get(VideoCaptureProperties.FrameHeight);
        Log.Print($"[Reframe] CenterCrop: input size {width}x{height}");

        double targetRatio = (double)AppConfig.OutputWidth / AppConfig.OutputHeight;
        double inputRatio = (double)width / height;

        int cropW, cropH;
        if (inputRatio > targetRatio)
        {
            cropH = height;
            cropW = (int)(height * targetRatio);
        }
        else
        {
            cropW = width;
            cropH = (int)(width / targetRatio);
        }

        int x = (width - cropW) / 2;
        int y = (height - cropH) / 2;

        Log.Print($"[Reframe] CenterCrop result: ({x},{y},{cropW},{cropH})");
        return (x, y, cropW, cropH);
    }

    private (int x, int y, int w, int h) FaceTrackCrop(
        string videoPath, double start, double duration)
    {
        Log.Print($"[Reframe] FaceTrackCrop entry: {videoPath}, start={start:F2}, duration={duration:F2}");
        var cascadePath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            "haarcascade_frontalface_default.xml");

        if (!System.IO.File.Exists(cascadePath))
        {
            Log.Warn($"[Reframe] FaceTrackCrop: cascade not at {cascadePath}, trying default");
            cascadePath = "haarcascade_frontalface_default.xml";
        }

        using var faceCascade = new CascadeClassifier(cascadePath);
        using var cap = new VideoCapture(videoPath);
        int width = (int)cap.Get(VideoCaptureProperties.FrameWidth);
        int height = (int)cap.Get(VideoCaptureProperties.FrameHeight);
        Log.Print($"[Reframe] FaceTrackCrop: input size {width}x{height}");

        double fps = cap.Get(VideoCaptureProperties.Fps);
        int startFrame = (int)(start * fps);
        int endFrame = (int)((start + duration) * fps);
        cap.Set(VideoCaptureProperties.PosFrames, startFrame);

        double targetRatio = (double)AppConfig.OutputWidth / AppConfig.OutputHeight;
        int cropW = height * targetRatio <= width
            ? (int)(height * targetRatio)
            : width;

        var facePositions = new List<int>();
        int step = Math.Max(1, (endFrame - startFrame) / 20);
        Log.Print($"[Reframe] FaceTrackCrop: frame range {startFrame}-{endFrame}, step={step}");

        using var gray = new Mat();
        for (int frameIdx = startFrame; frameIdx < endFrame; frameIdx += step)
        {
            cap.Set(VideoCaptureProperties.PosFrames, frameIdx);
            using var frame = new Mat();
            if (!cap.Read(frame)) break;

            Cv2.CvtColor(frame, gray, ColorConversionCodes.BGR2GRAY);
            var faces = faceCascade.DetectMultiScale(gray, 1.3, 5);

            if (faces.Length > 0)
            {
                var largest = faces[0];
                for (int i = 1; i < faces.Length; i++)
                {
                    if (faces[i].Width * faces[i].Height >
                        largest.Width * largest.Height)
                        largest = faces[i];
                }
                facePositions.Add(largest.X + largest.Width / 2);
            }
        }

        int x;
        if (facePositions.Count > 0)
        {
            int sum = 0;
            foreach (var pos in facePositions) sum += pos;
            int avgX = sum / facePositions.Count;
            x = Math.Max(0, Math.Min(avgX - cropW / 2, width - cropW));
            Log.Print($"[Reframe] FaceTrackCrop: {facePositions.Count} faces found, avgX={avgX}, final x={x}");
        }
        else
        {
            x = (width - cropW) / 2;
            Log.Warn($"[Reframe] FaceTrackCrop: no faces detected, falling back to center x={x}");
        }

        Log.Print($"[Reframe] FaceTrackCrop result: ({x},0,{cropW},{height})");
        return (x, 0, cropW, height);
    }
}
