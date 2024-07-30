using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace TexturePacker
{
    /// <summary>
    /// Represents a Texture in an atlas
    /// </summary>
    public class TextureInfo
    {
        /// <summary>
        /// Path of the source texture on disk
        /// </summary>
        public required string Source { get; init; }

        /// <summary>
        /// Width in Pixels
        /// </summary>
        public int Width { get; init; }

        /// <summary>
        /// Height in Pixels
        /// </summary>
        public int Height { get; init; }

        /// <summary>
        /// Alpha Paddings
        /// </summary>
        public required Rectangle Padding { get; init; }
    }

    /// <summary>
    /// Indicates in which direction to split an unused area when it gets used
    /// </summary>
    public enum SplitType
    {
        /// <summary>
        /// Split Horizontally (textures are stacked up)
        /// </summary>
        Horizontal,

        /// <summary>
        /// Split vertically (textures are side by side)
        /// </summary>
        Vertical,
    }

    /// <summary>
    /// Different types of heuristics in how to use the available space
    /// </summary>
    public enum BestFitHeuristic
    {
        /// <summary>
        /// 
        /// </summary>
        Area,

        /// <summary>
        /// 
        /// </summary>
        MaxOneAxis,
    }

    /// <summary>
    /// A node in the Atlas structure
    /// </summary>
    public class Node
    {
        /// <summary>
        /// Bounds of this node in the atlas
        /// </summary>
        public Rectangle Bounds;

        /// <summary>
        /// Texture this node represents
        /// </summary>
        public TextureInfo? Texture { get; set; }

        /// <summary>
        /// If this is an empty node, indicates how to split it when it will  be used
        /// </summary>
        public SplitType SplitType { get; set; }
    }

    /// <summary>
    /// The texture atlas
    /// </summary>
    public class Atlas
    {
        /// <summary>
        /// Width in pixels
        /// </summary>
        public int Width { get; set; }

        /// <summary>
        /// Height in Pixel
        /// </summary>
        public int Height { get; set; }

        /// <summary>
        /// List of the nodes in the Atlas. This will represent all the textures that are packed into it and all the remaining free space
        /// </summary>
        public List<Node> Nodes { get; } = [];
    }

    /// <summary>
    /// Objects that performs the packing task. Takes a list of textures as input and generates a set of atlas textures/definition pairs
    /// </summary>
    public class TexturePacker
    {
        public record struct Result(IReadOnlyList<Atlas> AtlasInfo, StringWriter Log, StringWriter ErrorLog);
        
        public static async Task<Result> PackAsync(string[] imagePaths, string outputDir,
            int atlasSize, int padding = 0, BestFitHeuristic fitHeuristic = BestFitHeuristic.Area)
        {
            var logger = new StringWriter();
            var errorLogger = new StringWriter();

            var atlasList =
                await ProcessAsync(imagePaths, atlasSize, padding, fitHeuristic, logger, errorLogger);
            await SaveAtlasesAsync(atlasList, outputDir);
            return new(atlasList, logger, errorLogger);
        }

        private static async Task<IReadOnlyList<Atlas>> ProcessAsync(string[] imagePaths, int atlasSize,
            int padding, BestFitHeuristic fitHeuristic, StringWriter logger, StringWriter errorLogger)
        {
            //1: scan for all the textures we need to pack
            var textures = await ScanForTexturesAsync(atlasSize, imagePaths, logger, errorLogger);

            //2: generate as many atlases as needed (with the latest one as small as possible)
            List<Atlas> atlases = [];
            while (textures.Count > 0)
            {
                var atlas = new Atlas
                {
                    Width = atlasSize,
                    Height = atlasSize
                };

                var leftovers = LayoutAtlas(textures, atlas, padding, fitHeuristic);

                if (leftovers.Count == 0)
                {
                    // we reached the last atlas. Check if this last atlas could have been twice smaller
                    while (leftovers.Count == 0)
                    {
                        atlas.Width /= 2;
                        atlas.Height /= 2;
                        leftovers = LayoutAtlas(textures, atlas, padding, fitHeuristic);
                    }

                    // we need to go 1 step larger as we found the first size that is to small
                    atlas.Width *= 2;
                    atlas.Height *= 2;
                    leftovers = LayoutAtlas(textures, atlas, padding, fitHeuristic);
                }

                atlases.Add(atlas);

                textures = leftovers;
            }

            return atlases;
        }

        private static async Task SaveAtlasesAsync(IReadOnlyList<Atlas> atlasList, string destination)
        {
            var atlasCount = 0;
            var prefix = destination.Replace(Path.GetExtension(destination), "");
            var dir = Path.GetDirectoryName(destination)!;
            Directory.CreateDirectory(dir);
            var encoder = new PngEncoder();
            foreach (var atlas in atlasList)
            {
                var atlasName = $"{prefix}{atlasCount:000}.png";
                var img = CreateAtlasImage(atlas);
                await img.SaveAsync(atlasName, encoder);
                ++atlasCount;
            }
        }

        private static async Task<List<TextureInfo>> ScanForTexturesAsync(int atlasSize, string[] imagePaths,
            StringWriter logger, StringWriter errorLogger)
        {
            var textures = new List<TextureInfo>();

            foreach (var path in imagePaths)
            {
                var img = await Image.LoadAsync<Argb32>(path);
                if (img.Width > atlasSize || img.Height > atlasSize)
                {
                    await errorLogger.WriteLineAsync(path + " is too large to fix in the atlas. Skipping!");
                    continue;
                }

                CalculateAlphaPadding(
                    img,
                    out var alphaBounds,
                    out var reducedWidth,
                    out var reducedHeight
                );

                if (reducedHeight == 0)
                {
                    reducedHeight++;
                    alphaBounds.Height--;
                }

                if (reducedWidth == 0)
                {
                    reducedWidth++;
                    alphaBounds.Width--;
                }
                
                var ti = new TextureInfo
                {
                    Source = path,
                    Width = reducedWidth,
                    Height = reducedHeight,
                    Padding = alphaBounds,
                };

                textures.Add(ti);

                await logger.WriteLineAsync("Added " + path);
            }
            
            return textures;
        }

        private static void CalculateAlphaPadding(
            Image<Argb32> image,
            out Rectangle alphaBounds,
            out int reducedWidth,
            out int reducedHeight)
        {
            var map = new bool[image.Width, image.Height];
            
            image.ProcessPixelRows(
                accessor =>
                {
                    for (var y = 0; y < image.Height; y++)
                    {
                        var rowSpan = accessor.GetRowSpan(y);
                        for (var x = 0; x < image.Width; x++)
                        {
                            var pixel = rowSpan[x];
                            map[x, y] = pixel.A != 0;
                        }
                    } 
                }
            );

            CalculateAlphaPaddingImpl(map, out var left, out var right, out var top, out var bottom);
            
            alphaBounds = Rectangle.FromLTRB(left, top, right, bottom);

            reducedWidth = image.Width - left - right;
            reducedHeight = image.Height - top - bottom;
        }

        private static void CalculateAlphaPaddingImpl(
            bool[,] image,
            out int left,
            out int right,
            out int top,
            out int bottom)
        {
            var width = image.GetLength(0);
            var height = image.GetLength(1);
            
            left = 0;
            right = 0;
            top = 0;
            bottom = 0;
            
            for (var y = height - 1; y >= 0; y--)
            {
                var isTransparent = true;
                
                for (var x = 0; x < width; x++)
                {
                    if (!image[x, y]) continue;
                    isTransparent = false;
                    break;
                }

                if (!isTransparent) break;
                bottom++;
            }

            for (var y = 0; y < height; y++)
            {
                var isTransparent = true;
                
                for (var x = 0; x < width; x++)
                {
                    if (!image[x, y]) continue;
                    isTransparent = false;
                    break;
                }

                if (!isTransparent) break;
                top++;
            }

            for (var x = width - 1; x >= 0; x--)
            {
                var isTransparent = true;

                for (var y = 0; y < height; y++)
                {
                    if (!image[x, y]) continue;
                    isTransparent = false;
                    break;
                }
                
                if (!isTransparent) break;
                right++;
            }

            for (var x = 0; x < width; x++)
            {
                var isTransparent = true;
                
                
                for (var y = 0; y < height; y++)
                {
                    if (!image[x, y]) continue;
                    isTransparent = false;
                    break;
                }

                if (!isTransparent) break;
                left++;
            }

            if (left + right <= width) return;
            
            left = width / 2;
            right = width - left;
            top = height / 2;
            bottom = height - top;
        }
        
        private static void HorizontalSplit(Node toSplit, int width, int height, int padding, List<Node> list)
        {
            var n1 = new Node();
            n1.Bounds.X = toSplit.Bounds.X + width + padding;
            n1.Bounds.Y = toSplit.Bounds.Y;
            n1.Bounds.Width = toSplit.Bounds.Width - width - padding;
            n1.Bounds.Height = height;
            n1.SplitType = SplitType.Vertical;

            var n2 = new Node();
            n2.Bounds.X = toSplit.Bounds.X;
            n2.Bounds.Y = toSplit.Bounds.Y + height + padding;
            n2.Bounds.Width = toSplit.Bounds.Width;
            n2.Bounds.Height = toSplit.Bounds.Height - height - padding;
            n2.SplitType = SplitType.Horizontal;

            if (n1.Bounds is { Width: > 0, Height: > 0 })
                list.Add(n1);
            if (n2.Bounds is { Width: > 0, Height: > 0 })
                list.Add(n2);
        }

        private static void VerticalSplit(Node toSplit, int width, int height, int padding, List<Node> list)
        {
            var n1 = new Node();
            n1.Bounds.X = toSplit.Bounds.X + width + padding;
            n1.Bounds.Y = toSplit.Bounds.Y;
            n1.Bounds.Width = toSplit.Bounds.Width - width - padding;
            n1.Bounds.Height = toSplit.Bounds.Height;
            n1.SplitType = SplitType.Vertical;

            var n2 = new Node();
            n2.Bounds.X = toSplit.Bounds.X;
            n2.Bounds.Y = toSplit.Bounds.Y + height + padding;
            n2.Bounds.Width = width;
            n2.Bounds.Height = toSplit.Bounds.Height - height - padding;
            n2.SplitType = SplitType.Horizontal;

            if (n1.Bounds is { Width: > 0, Height: > 0 })
                list.Add(n1);
            if (n2.Bounds is { Width: > 0, Height: > 0 })
                list.Add(n2);
        }

        private static TextureInfo? FindBestFitForNode(Node node, List<TextureInfo> textures,
            BestFitHeuristic fitHeuristic)
        {
            TextureInfo? bestFit = null;

            float nodeArea = node.Bounds.Width * node.Bounds.Height;
            var maxCriteria = 0.0f;

            foreach (var ti in textures)
            {
                switch (fitHeuristic)
                {
                    // Max of Width and Height ratios
                    case BestFitHeuristic.MaxOneAxis:
                        if (ti.Width > node.Bounds.Width || ti.Height > node.Bounds.Height)
                            break;

                        var wRatio = ti.Width / (float)node.Bounds.Width;
                        var hRatio = ti.Height / (float)node.Bounds.Height;
                        var ratio = wRatio > hRatio ? wRatio : hRatio;

                        if (!(ratio > maxCriteria))
                            break;

                        maxCriteria = ratio;
                        bestFit = ti;

                        break;

                    // Maximize Area coverage
                    case BestFitHeuristic.Area:

                        if (ti.Width > node.Bounds.Width || ti.Height > node.Bounds.Height)
                            break;

                        float textureArea = ti.Width * ti.Height;
                        var coverage = textureArea / nodeArea;

                        if (!(coverage > maxCriteria))
                            break;

                        maxCriteria = coverage;
                        bestFit = ti;

                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(fitHeuristic), fitHeuristic, null);
                }
            }

            return bestFit;
        }

        private static List<TextureInfo> LayoutAtlas(List<TextureInfo> textureList, Atlas atlas, int padding,
            BestFitHeuristic fitHeuristic)
        {
            var freeList = new List<Node>();
            var textures = textureList.ToList();

            var root = new Node();
            atlas.Nodes.Clear();
            root.Bounds.Size = new Size(atlas.Width, atlas.Height);
            root.SplitType = SplitType.Horizontal;

            freeList.Add(root);

            while (freeList.Count > 0 && textures.Count > 0)
            {
                var node = freeList[0];
                freeList.RemoveAt(0);

                var bestFit = FindBestFitForNode(node, textures, fitHeuristic);
                
                if (bestFit == null) continue;
                
                if (node.SplitType == SplitType.Horizontal)
                {
                    HorizontalSplit(node, bestFit.Width, bestFit.Height, padding, freeList);
                }
                else
                {
                    VerticalSplit(node, bestFit.Width, bestFit.Height, padding, freeList);
                }

                node.Texture = bestFit;
                node.Bounds.Width = bestFit.Width;
                node.Bounds.Height = bestFit.Height;

                textures.Remove(bestFit);
                atlas.Nodes.Add(node);
            }

            return textures;
        }

        private static Image<Argb32> CreateAtlasImage(Atlas atlas)
        {
            var img = new Image<Argb32>(atlas.Width, atlas.Height);

            img.Mutate(context =>
            {
                foreach (var node in atlas.Nodes)
                {
                    var bounds = node.Bounds;
                    if (node.Texture != null)
                    {
                        var sourceImg = Image.Load<Argb32>(node.Texture.Source);
                        sourceImg.Mutate(sourceContext =>
                        {
                            var imageRectangle = sourceImg.Bounds;
                            var texturePadding = node.Texture.Padding;
                            var cropRectangle = Rectangle.FromLTRB(
                                imageRectangle.Left + texturePadding.Left,
                                imageRectangle.Top + texturePadding.Top,
                                imageRectangle.Right - texturePadding.Right,
                                imageRectangle.Bottom - texturePadding.Bottom
                            );
                            sourceContext.Crop(cropRectangle);
                        });
                        context.DrawImage(sourceImg, bounds.Location, 1);
                    }
                    else
                    {
                        context.Fill(Color.DarkMagenta, bounds);
                    }
                }
            });
            
            return img;
        }
    }
}