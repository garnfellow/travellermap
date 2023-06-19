#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Xml.Serialization;

namespace Maps
{
    [Serializable]
    public class MapNotInitializedException : Exception
    {
        public MapNotInitializedException() : base("SectorMap data not initialized") { }
        public MapNotInitializedException(string message) : base(message) { }
        public MapNotInitializedException(string message, Exception innerException) : base(message, innerException) { }
        protected MapNotInitializedException(SerializationInfo info, StreamingContext context) : base(info, context) { }
    }

    internal struct SectorMetafileEntry
    {
        public string filename;
        public List<string> tags;
        public SectorMetafileEntry(string filename, List<string> tags)
        {
            this.tags = tags;
            this.filename = filename;
        }
    }

    internal class SectorMap
    {
        private static object s_lock = new object();

        /// <summary>
        /// Singleton - initialized once and retained for the life of the application.
        /// </summary>
        private static SectorMap? s_instance;

        /// <summary>
        /// Holds all known sectors across all milieux.
        /// </summary>
        private readonly SectorCollection sectors = new SectorCollection();

        /// <summary>
        /// Enumerate all known sectors across all milieux. Callers should generally not use this, since it
        /// will contain duplicate sectors across milieux.
        /// </summary>
        public IEnumerable<Sector> Sectors => sectors.Sectors;

        /// <summary>
        /// Represents a single milieu. Contains maps from name to Sector and coordinates to Sector.
        /// </summary>
        internal class MilieuMap
        {
            public MilieuMap(string name) { Name = name; }
            public string Name { get; }

            private ConcurrentDictionary<string, Sector> nameMap = new ConcurrentDictionary<string, Sector>(StringComparer.InvariantCultureIgnoreCase);
            private ConcurrentDictionary<Point, Sector> locationMap = new ConcurrentDictionary<Point, Sector>();

            public Sector FromName(string name)
            {
                nameMap.TryGetValue(name, out Sector sector);
                return sector;
            }

            public Sector FromLocation(Point coords)
            {
                locationMap.TryGetValue(coords, out Sector sector);
                return sector;
            }

            public void TryAdd(Sector sector)
            {
                lock (this)
                {
                    if (!locationMap.TryAdd(sector.Location, sector))
                        return;

                    sector.MilieuMap = this;

                    foreach (var name in sector.Names)
                    {
                        if (name.Text != null)
                        {
                            nameMap.TryAdd(name.Text, sector);

                            // Automatically alias "SpinwardMarches"
                            nameMap.TryAdd(name.Text.Replace(" ", ""), sector);
                        }
                    }

                    lock (sector)
                    {
                        if (!string.IsNullOrEmpty(sector.Abbreviation))
                        {
                            nameMap.TryAdd(sector.Abbreviation ?? "", sector);
                        }
                        else
                        {
                            // Synthesize an abbreviation, e.g. "Cent"
                            string? abbrev = sector.SynthesizeAbbreviation();
                            if (abbrev != null)
                            {
                                if (nameMap.TryAdd(abbrev, sector))
                                {
                                    sector.Abbreviation = abbrev;
                                }
                                else
                                {
                                    // But if that's used, try "Cen2", etc.
                                    for (int i = 2; i <= 99; ++i)
                                    {
                                        string suffix = i.ToString();
                                        string prefix = abbrev.Substring(0, 4 - suffix.Length);
                                        if (nameMap.TryAdd(prefix + suffix, sector))
                                        {
                                            sector.Abbreviation = prefix + suffix;
                                            break;
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }

        public const string DEFAULT_MILIEU = "M1105";

        /// <summary>
        /// Holds all milieu, keyed by name (e.g. "M0").
        /// </summary>
        private ConcurrentDictionary<string, MilieuMap> milieux
            = new ConcurrentDictionary<string, MilieuMap>(StringComparer.InvariantCultureIgnoreCase);

        private MilieuMap GetMilieuMap(string name) => milieux.GetOrAdd(name, n => new MilieuMap(n));

        public IEnumerable<string> GetMilieux() => milieux.Keys;

        // Singleton initialization
        private SectorMap(IEnumerable<SectorMetafileEntry> metafiles)
        {
            // Load all sectors from all metafiles.
            foreach (var metafile in metafiles)
            {
                if (!(ResourceManager.GetXmlFileObject<SectorCollection>(metafile.filename) is SectorCollection collection))
                    throw new ApplicationException($"Invalid file: {metafile.filename}");

                foreach (var sector in collection.Sectors)
                {
                    sector.Tags.AddRange(metafile.tags);
                    sector.AdjustRelativePaths(metafile.filename);
                }
                sectors.Merge(collection);
            }

            // Create/populate individual milieu maps.
            foreach (var sector in sectors.Sectors)
            {
                if (sector.MetadataFile != null)
                {
                    if (!(ResourceManager.GetXmlFileObject<Sector>(sector.MetadataFile) is Sector metadata))
                        throw new ApplicationException($"Invalid file: {sector.MetadataFile}");

                    metadata.AdjustRelativePaths(sector.MetadataFile);
                    sector.Merge(metadata);
                }

                GetMilieuMap(sector.CanonicalMilieu).TryAdd(sector);
            }
        }

        // Singleton accessor
        public static SectorMap GetInstance()
        {
            lock (SectorMap.s_lock)
            {
                if (s_instance == null)
                {
                    List<SectorMetafileEntry> files = new List<SectorMetafileEntry>();

                    using var reader = File.OpenText(System.Web.Hosting.HostingEnvironment.MapPath(@"~/res/Sectors/milieu.tab"));
                    var parser = new Serialization.TSVParser(reader);
                    foreach (var row in parser.Data)
                    {
                        var path = row.dict["Path"];
                        var tags = row.dict["Tags"].Split(',');
                        files.Add(new SectorMetafileEntry(@"~/res/Sectors/" + path, tags.ToList()));
                    }

                    s_instance = new SectorMap(files);
                }
            }

            return s_instance;
        }

        public static void Flush()
        {
            lock (SectorMap.s_lock)
            {
                s_instance = null;
            }
        }

        // This method supports deserializing of Location instances that reference sectors by name.
        // Throws if the map is not initialized.
        public static Point GetSectorCoordinatesByName(string name)
        {
            SectorMap? instance;
            lock (SectorMap.s_lock)
            {
                instance = s_instance;
            }
            if (instance == null)
                throw new MapNotInitializedException();
            Sector? sector = instance.FromName(name, null);
            if (sector == null)
                throw new ApplicationException($"Sector not found: {name}");
            return sector.Location;
        }

        /// <summary>
        /// Proxy for clients that want to perform lookups within a milieu (the most common case).
        /// This holds a SectorMap/milieu name pair.
        /// </summary>
        public class Milieu
        {
            private SectorMap map;
            private string? milieu;
            public Milieu(SectorMap map, string? milieu)
            {
                this.map = map;
                this.milieu = milieu;
            }
            public Sector? FromLocation(int x, int y, bool useMilieuFallbacks = false)
                => map.FromLocation(new Point(x, y), milieu, useMilieuFallbacks);
            public Sector? FromLocation(Point pt, bool useMilieuFallbacks = false)
                => map.FromLocation(pt, milieu, useMilieuFallbacks);
            public Sector? FromName(string name)
                => map.FromName(name, milieu);
        }

        public static Milieu ForMilieu(string? milieu)
            => new Milieu(SectorMap.GetInstance(), milieu);

        /// <summary>
        /// Helper to find MilieuMaps by name.
        /// </summary>
        /// <param name="m">Specific milieu. If specified, at most one milieu will be returned.
        /// If null, default/fallback milieu will be returned</param>
        /// <returns>Enumerable yielding all matching MilieuMap instances.</returns>
        private IEnumerable<MilieuMap> SelectMilieux(string? m)
        {
            if (m != null)
            {
                // If milieu name is specified, return matching MilieuMap if found.
                if (milieux.ContainsKey(m))
                    yield return milieux[m];
            }
            else
            {
                yield return milieux[DEFAULT_MILIEU];
            }
        }

        /// <summary>
        /// Finds sector by name in the named milieu (using default/fallbacks if null)
        /// </summary>
        /// <param name="name">Sector name</param>
        /// <param name="milieu">Milieu name, null for default/fallbacks</param>
        /// <returns>Sector if found, or null</returns>
        private Sector? FromName(string name, string? milieu) => SelectMilieux(milieu)
                    .Select(m => m.FromName(name))
                    .Where(s => s != null)
                    .FirstOrDefault();

        /// <summary>
        /// Finds sector by location in the named milieu (using default/fallbacks if null)
        /// </summary>
        /// <param name="x">Sector x coordinate</param>
        /// <param name="y">Sector y coordinate</param>
        /// <param name="milieu">Milieu name, null for default</param>
        /// <returns>Sector if found, or null</returns>
        private Sector? FromLocation(Point pt, string? milieu, bool useMilieuFallbacks = false)
        {
            Sector? sector = SelectMilieux(milieu)
                .Select(map => map.FromLocation(pt))
                .Where(sec => sec != null && (useMilieuFallbacks || !(sec is Dotmap)))
                .FirstOrDefault();

            if (sector != null || milieu == null || !useMilieuFallbacks)
                return sector;

            // Fall back to default milieu and produce a dotmap
            sector = FromLocation(pt, null);
            if (sector == null)
                return null;
            sector = new Dotmap(sector);

            // Remember it, if milieu is known.
            if (milieux.ContainsKey(milieu))
                milieux[milieu].TryAdd(sector);

            return sector;
        }
    }

    [XmlRoot(ElementName = "Sectors")]
    public class SectorCollection
    {
        [XmlElement("Sector")]
        public List<Sector> Sectors { get; } = new List<Sector>();

        public void Merge(SectorCollection otherCollection)
        {
            if (otherCollection == null)
                throw new ArgumentNullException(nameof(otherCollection));

            if (otherCollection.Sectors != null)
                Sectors.AddRange(otherCollection.Sectors);
        }
    }
}