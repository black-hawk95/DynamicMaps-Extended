using System;

namespace DynamicMapsExtended.Api
{
    /// <summary>
    /// Public lookup helpers for other mods integrating with DynamicMaps Extended map definitions.
    /// </summary>
    public static class Maps
    {
        /// <summary>
        /// Version of this public API contract, independent of the plugin's release version.
        /// </summary>
        public const string ApiVersion = "1.0.0";

        /// <summary>
        /// Resolves a location ID to the corresponding DynamicMaps internal map name when
        /// DynamicMaps Extended provides a dedicated definition for that backport.
        /// </summary>
        /// <param name="locationId">The canonical SPT location ID, such as "interchange".</param>
        /// <param name="backport">
        /// True only when the caller has independently determined that the backport is selected.
        /// </param>
        /// <returns>
        /// The DynamicMaps Extended alias for a supported backport; otherwise the original
        /// locationId, unchanged.
        /// </returns>
        /// <remarks>
        /// This method only resolves names. It does not determine which variant is selected
        /// and does not verify that the backport plugin or Extended assets are installed.
        /// Use the relevant variant API (for example, MapVariants) to determine the selection.
        /// </remarks>
        public static string MapInternalName(string locationId, bool backport)
        {
            if (string.IsNullOrWhiteSpace(locationId) || !backport)
                return locationId;

            switch (locationId.Trim().ToLowerInvariant())
            {
                case "interchange":
                    return MapAliasPatch.InterchangeAlias;
                case "laboratory":
                    return MapAliasPatch.LabsAlias;
                default:
                    return locationId;
            }
        }
    }
}
