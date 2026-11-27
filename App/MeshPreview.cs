using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Rbx2Source.Export;

namespace Rbx2Source
{
    // The mesh preview can be turned: dragging it turns the mesh (quickly drawn while dragging, then drawn smoothly when let
    // go), and double-clicking puts it back at the angle Roblox's thumbnails use. The view it is left at is the preview's
    // picture, which the Library keeps for the export.
    public sealed partial class MainWindow
    {
        private MeshRender previewMesh;
        private string previewMeshDescription;
        private double previewYaw = MeshRender.DefaultYaw, previewPitch = MeshRender.DefaultPitch;
        private bool turningMesh, drawingMesh, meshRedrawWaiting, meshRedrawSmooth;
        private Point lastTurn;
        // Degrees turned for each pixel dragged.
        private const double TurnPerPixel = 0.6;
        private const int PreviewSize = 256;

        private void WireMeshPreview()
        {
            previewFrame.PointerPressed += (sender, args) =>
            {
                if (previewMesh == null || !args.GetCurrentPoint(previewFrame).Properties.IsLeftButtonPressed)
                    return;
                if (args.ClickCount == 2)
                {
                    // Double-click: back to the thumbnail's own angle.
                    previewYaw = MeshRender.DefaultYaw;
                    previewPitch = MeshRender.DefaultPitch;
                    RedrawMesh(true);
                    args.Handled = true;
                    return;
                }
                turningMesh = true;
                lastTurn = args.GetPosition(previewFrame);
                args.Pointer.Capture(previewFrame);
                args.Handled = true;
            };
            previewFrame.PointerMoved += (sender, args) =>
            {
                if (!turningMesh || previewMesh == null)
                    return;
                Point now = args.GetPosition(previewFrame);
                // Dragging right turns the mesh to the right (the camera goes the other way); dragging down tips its top
                // towards you.
                previewYaw -= (now.X - lastTurn.X) * TurnPerPixel;
                previewPitch = Math.Max(-85, Math.Min(85, previewPitch + (now.Y - lastTurn.Y) * TurnPerPixel));
                lastTurn = now;
                RedrawMesh(false);
            };
            previewFrame.PointerReleased += (sender, args) =>
            {
                if (!turningMesh)
                    return;
                turningMesh = false;
                args.Pointer.Capture(null);
                RedrawMesh(true);
            };
            previewFrame.PointerCaptureLost += (sender, args) =>
            {
                if (!turningMesh)
                    return;
                turningMesh = false;
                RedrawMesh(true);
            };
        }

        // Shows a mesh preview that can be turned, drawn at the thumbnail's angle.
        private void ShowMeshPreview(MeshRender mesh, string description)
        {
            previewYaw = MeshRender.DefaultYaw;
            previewPitch = MeshRender.DefaultPitch;
            previewMeshDescription = description;
            previewMesh = mesh;
            previewFrame.Cursor = new Cursor(StandardCursorType.SizeAll);
            RedrawMesh(true);
        }

        // Forgets the turnable mesh (another preview replaced it).
        private void ForgetMeshPreview()
        {
            previewMesh = null;
            turningMesh = false;
            previewFrame.Cursor = Cursor.Default;
        }

        // Draws the mesh at the current angle: quickly (one sample a pixel) while it is being turned, smoothly otherwise.
        // One drawing runs at a time; turns made meanwhile are drawn once it finishes.
        private async void RedrawMesh(bool smooth)
        {
            MeshRender mesh = previewMesh;
            if (mesh == null)
                return;
            if (drawingMesh)
            {
                meshRedrawWaiting = true;
                meshRedrawSmooth |= smooth;
                return;
            }
            drawingMesh = true;
            try
            {
                double yaw = previewYaw, pitch = previewPitch;
                byte[] picture = await Task.Run(() =>
                {
                    using (Picture render = mesh.Draw(PreviewSize, yaw, pitch, smooth ? MeshRender.Samples : 1))
                        return render?.Encode(PictureFormat.Png);
                });
                if (picture != null && mesh == previewMesh)
                {
                    ShowPreview(ToBitmap(picture), previewMeshDescription + "\nDrag to turn it; double-click to reset.", picture, keepMesh: true);
                }
            }
            catch (Exception error) when (error is InvalidOperationException || error is ArgumentException)
            {
                // A drawing that fails leaves the last picture.
            }
            finally
            {
                drawingMesh = false;
            }
            if (meshRedrawWaiting)
            {
                bool again = meshRedrawSmooth || !turningMesh;
                meshRedrawWaiting = false;
                meshRedrawSmooth = false;
                RedrawMesh(again);
            }
        }
    }
}
