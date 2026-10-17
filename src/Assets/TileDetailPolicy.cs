namespace DynamicMapsExtended
{
    internal static class TileDetailPolicy
    {
        // Keep roughly 1.25 texture pixels per screen pixel. Hysteresis prevents
        // switching packs repeatedly around a zoom boundary. Panning does not affect scale.
        internal static int ChooseFactor(float screenPixelsPerSourcePixel, int currentFactor)
        {
            if (screenPixelsPerSourcePixel <= 0f) return currentFactor > 0 ? currentFactor : 1;
            var currentDensity = screenPixelsPerSourcePixel * currentFactor;
            if (currentFactor > 0 && currentDensity >= 0.35f && currentDensity <= 0.9f)
                return currentFactor;
            var factor = 1;
            while (factor < 8 && screenPixelsPerSourcePixel * factor * 2f <= 0.8f)
                factor *= 2;
            return factor;
        }
    }
}
