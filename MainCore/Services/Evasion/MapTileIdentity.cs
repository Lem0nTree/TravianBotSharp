namespace MainCore.Services.Evasion;

public static class MapTileIdentity
{
    // Travian tile d is a map-grid index, not the village's newdid identifier.
    public static long? TargetTile(long sourceTile, int sourceX, int sourceY, int targetX, int targetY)
    {
        if (sourceTile <= 0) return null;
        long? result = null;
        for (var radius = 1; radius <= 2000; radius++)
        {
            if (Math.Abs((long)sourceX) > radius || Math.Abs((long)sourceY) > radius ||
                Math.Abs((long)targetX) > radius || Math.Abs((long)targetY) > radius) continue;
            long width = 2L * radius + 1;
            long observed = (radius - (long)sourceY) * width + sourceX + radius + 1;
            if (observed != sourceTile) continue;
            var target = (radius - (long)targetY) * width + targetX + radius + 1;
            if (result is not null) return null;
            result = target;
        }
        return result;
    }
}
