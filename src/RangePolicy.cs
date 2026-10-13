using System;
using System.Collections.Generic;

namespace TerraNilTrainer
{
    // 范围算法独立于游戏引擎，便于检查边界和恢复行为。
    public struct Cell : IEquatable<Cell>
    {
        public int X, Z;
        public Cell(int x, int z) { X = x; Z = z; }
        public bool Equals(Cell other) { return X == other.X && Z == other.Z; }
        public override bool Equals(object obj) { return obj is Cell && Equals((Cell)obj); }
        public override int GetHashCode() { return unchecked(X * 397 ^ Z); }
    }
    public static class RangePolicy
    {
        public static int ClampFactor(int value) { return Math.Max(1, Math.Min(5, value)); }
        public static int ScaleRadius(int original, int factor, int mapSize)
        {
            if (original <= 0 || factor <= 1) return original;
            // 圆形扫描最多到地图对角线，防止生成大量地图外坐标。
            int limit = mapSize > 0 ? (int)Math.Ceiling(mapSize * Math.Sqrt(2)) : 256;
            return (int)Math.Min((long)original * ClampFactor(factor), limit);
        }
        public static HashSet<Cell> ExpandArea(IEnumerable<Cell> original, int originX, int originZ,
            int width, int height, int factor, int mapSize)
        {
            factor = ClampFactor(factor);
            HashSet<Cell> area = new HashSet<Cell>();
            double centerX = originX + width / 2.0, centerZ = originZ + height / 2.0;
            foreach (Cell p in original)
            {
                // 缩放每个格子的边界并填充内部，避免放大后出现棋盘状空洞。
                int minX = (int)Math.Floor(centerX + (p.X - centerX) * factor);
                int maxX = (int)Math.Ceiling(centerX + (p.X + 1 - centerX) * factor) - 1;
                int minZ = (int)Math.Floor(centerZ + (p.Z - centerZ) * factor);
                int maxZ = (int)Math.Ceiling(centerZ + (p.Z + 1 - centerZ) * factor) - 1;
                for (int x = Math.Max(0, minX); x <= Math.Min(mapSize - 1, maxX); x++)
                    for (int z = Math.Max(0, minZ); z <= Math.Min(mapSize - 1, maxZ); z++)
                        area.Add(new Cell(x, z));
            }
            // 扩大时仍保留原区域，特别是倍率为偶数、建筑中心落在半格时。
            foreach (Cell p in original)
                if (p.X >= 0 && p.Z >= 0 && p.X < mapSize && p.Z < mapSize) area.Add(p);
            return area;
        }
        public static bool FootprintInside(int x, int z, int width, int height, int mapSize)
        {
            return width > 0 && height > 0 && mapSize > 0 && x >= 0 && z >= 0 &&
                (long)x + width <= mapSize && (long)z + height <= mapSize;
        }
    }
}
