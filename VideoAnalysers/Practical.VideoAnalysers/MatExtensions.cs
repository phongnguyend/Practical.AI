using OpenCvSharp;

public static class MatExtensions
{
    public static string ToBase64(this Mat mat, ImwriteFlags imageFormat = ImwriteFlags.JpegQuality)
    {
        // Convert the frame to grayscale
        using var gray = new Mat();
        Cv2.CvtColor(mat, gray, ColorConversionCodes.BGR2GRAY);

        // Encode Mat to memory buffer
        Cv2.ImEncode(".jpg", gray, out var imageData); // ".jpg" or ".png"

        // Convert buffer to base64
        return Convert.ToBase64String(imageData);
    }
}
