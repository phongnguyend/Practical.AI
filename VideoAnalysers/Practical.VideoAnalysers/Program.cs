using DlibDotNet;
using OpenCvSharp;
using System.Diagnostics;


// Open the default camera (usually the first camera)
using var capture = new VideoCapture(0);
if (!capture.IsOpened())
{
    Console.WriteLine("Camera not found!");
    return;
}

// load a cascade file for detecting faces
var faceCascade = new CascadeClassifier("haarcascade_frontalface_default.xml");
var eyeCascade = new CascadeClassifier("haarcascade_eye.xml");

var faceDetector = Dlib.GetFrontalFaceDetector();
var shapePredictor = ShapePredictor.Deserialize("shape_predictor_68_face_landmarks.dat");

// Create a window to display the camera feed
using var window = new Window("Camera");

// Create a Mat object to hold the frame data
using var frame = new Mat();

// Create a Stopwatch to measure time
var stopwatch = new Stopwatch();
int frameCount = 0;
double fps = 0.0;

DateTime? drowsyStartTime = null;
TimeSpan drowsyThreshold = TimeSpan.FromSeconds(3);

while (true)
{
    stopwatch.Restart();

    // Capture a frame from the camera
    capture.Read(frame);

    // If the frame is empty, break the loop
    if (frame.Empty())
        break;

    frame.SaveImage("temp.jpg");

    var dlibImage = Dlib.LoadImage<RgbPixel>("temp.jpg");

    var faces = faceDetector.Operator(dlibImage);

    foreach (var face in faces)
    {
        var shape = shapePredictor.Detect(dlibImage, face);

        var leftEAR = CalculateEAR(shape, 42); // left eye starts at point 42
        var rightEAR = CalculateEAR(shape, 36); // right eye starts at point 36

        double avgEAR = (leftEAR + rightEAR) / 2.0;

        if (avgEAR < 0.25)
        {
            if (drowsyStartTime == null)
            {
                drowsyStartTime = DateTime.Now; // start timer
            }
            else if (DateTime.Now - drowsyStartTime > drowsyThreshold)
            {
                // Been drowsy for > 3 seconds
                Cv2.PutText(frame, "Drowsy!", new OpenCvSharp.Point(face.Left, face.Top - 10), HersheyFonts.HersheySimplex, 1, Scalar.Red, 2);
            }
        }
        else
        {
            // Eyes open → reset timer
            drowsyStartTime = null;
        }

        // Draw landmarks
        for (int i = 0; i < shape.Parts; i++)
        {
            var point = shape.GetPart((uint)i);
            Cv2.Circle(frame, new OpenCvSharp.Point(point.X, point.Y), 2, Scalar.Lime, -1);
        }
    }

    //// Convert the frame to grayscale
    //using var gray = new Mat();
    //Cv2.CvtColor(frame, gray, ColorConversionCodes.BGR2GRAY);

    //// Detect faces
    //var faces = faceCascade.DetectMultiScale(gray, 1.3, 5);

    //foreach (var face in faces)
    //{
    //    // Draw a rectangle around the face
    //    Cv2.Rectangle(frame, face, new Scalar(0, 0, 255), 2);

    //    //// Extract the face region
    //    //var faceRegion = new Rect(face.X, face.Y, face.Width, face.Height);
    //    //using var faceMat = new Mat(frame, faceRegion);

    //    //// Apply Gaussian blur to the face region
    //    //Cv2.GaussianBlur(faceMat, faceMat, new Size(23, 23), 30);

    //    //// Merge the blurry face back to the frame
    //    //faceMat.CopyTo(new Mat(frame, faceRegion));

    //    var faceROI = new Mat(gray, face);
    //    var eyes = eyeCascade.DetectMultiScale(faceROI);

    //    int closedEyes = 0;
    //    foreach (var eye in eyes)
    //    {
    //        var eyeRect = new Rect(
    //            face.X + eye.X,
    //            face.Y + eye.Y,
    //            eye.Width,
    //            eye.Height
    //        );

    //        Cv2.Rectangle(frame, eyeRect, Scalar.Green, 2);

    //        // Very basic heuristic: if eye height is very small, it's closed
    //        if (eye.Height < eye.Width / 3)
    //            closedEyes++;
    //    }
    //}


    // Calculate FPS
    frameCount++;
    if (frameCount >= 10)
    {
        stopwatch.Stop();
        fps = frameCount / (stopwatch.ElapsedMilliseconds / 1000.0);
        frameCount = 0;
    }

    // Display FPS on the frame
    Cv2.PutText(frame, $"FPS: {fps:F2}", new OpenCvSharp.Point(10, 30), HersheyFonts.HersheySimplex, 1, Scalar.White, 2);

    // Show the frame in the window
    window.ShowImage(frame);

    // Wait for 1 ms and check if the 'q' key is pressed
    if (Cv2.WaitKey(1) == 'q')
        break;
}

static double CalculateEAR(FullObjectDetection shape, int startIndex)
{
    var p1 = ToPoint(shape.GetPart((uint)(startIndex + 0)));
    var p2 = ToPoint(shape.GetPart((uint)(startIndex + 1)));
    var p3 = ToPoint(shape.GetPart((uint)(startIndex + 2)));
    var p4 = ToPoint(shape.GetPart((uint)(startIndex + 3)));
    var p5 = ToPoint(shape.GetPart((uint)(startIndex + 4)));
    var p6 = ToPoint(shape.GetPart((uint)(startIndex + 5)));

    double vertical1 = Distance(p2, p6);
    double vertical2 = Distance(p3, p5);
    double horizontal = Distance(p1, p4);

    return (vertical1 + vertical2) / (2.0 * horizontal);
}

static Point2d ToPoint(DlibDotNet.Point point)
{
    return new Point2d(point.X, point.Y);
}

static double Distance(Point2d p1, Point2d p2)
{
    double dx = p1.X - p2.X;
    double dy = p1.Y - p2.Y;
    return Math.Sqrt(dx * dx + dy * dy);
}