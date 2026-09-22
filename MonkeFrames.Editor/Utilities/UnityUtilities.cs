using System;
using UnityEngine;

namespace MonkeFrames.Editor.Utilities
{
    public static class UnityUtilities
    {
        public static string Vector3ToString(Vector3 vec)
        {
            return $"({vec.x}, {vec.y}, {vec.z})";
        }

        public static string ColorToString(Color color)
        {
            if (color == Color.red)
                return "Red";

            if (color == Color.orange)
                return "Orange";

            if (color == Color.yellow)
                return "Yellow";

            if (color == Color.green)
                return "Green";

            if (color == Color.cyan)
                return "Cyan";

            if (color == Color.blue)
                return "Blue";

            if (color == Color.magenta)
                return "Pink";

            if (color == Color.purple)
                return "Purple";

            return color.ToString();
        }

        public static Texture2D CreateTexture(byte[] img)
        {
            Texture2D tex = new Texture2D(2, 2);
            bool isLoaded = tex.LoadImage(img);

            tex.LoadImage(img);
            return tex;
            
            throw new Exception("Image failed to load");
        }
    }
}
