﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;

using BnsBinTool.Core;
using BnsBinTool.Core.DataStructs;
using BnsBinTool.Core.Definitions;
using BnsBinTool.Core.Helpers;
using BnsBinTool.Core.Models;
using BnsBinTool.Xml.AliasResolvers;
using BnsBinTool.Xml.Helpers;

using K4os.Hash.xxHash;

namespace BnsBinTool.Xml.Converters
{
    public class XmlToDatafileConverter
    {
        // Init only - READ ONLY
        private readonly DatafileDefinition _datafileDef;
        private readonly string _datafilePath;
        private readonly string _localfilePath;
        private readonly string _projectRoot;
        private readonly string _extractedXmlDatPath;
        private readonly string _extractedLocalDatPath;
        private RecordBuilder _recordBuilder;
        private Datafile _data;
        private Datafile _local;
        private ResolvedAliases _resolvedAliases;

        // Hashing
        private readonly Dictionary<string, ulong> _modifiedHashes = new Dictionary<string, ulong>();
        private readonly Dictionary<string, ulong> _hashes;
        private readonly MemoryStream _buffer = new MemoryStream();

        // States
        private Dictionary<int, Table> _tables;

        public XmlToDatafileConverter(string projectRoot, DatafileDefinition datafileDef, string datafilePath, string localfilePath, string extractedXmlDatPath, string extractedLocalDatPath)
        {
            _projectRoot = Path.GetFullPath(projectRoot);
            _datafileDef = datafileDef;
            _datafilePath = datafilePath;
            _localfilePath = localfilePath;
            _extractedXmlDatPath = extractedXmlDatPath;
            _extractedLocalDatPath = extractedLocalDatPath;

            if (File.Exists($"{_projectRoot}\\hashes.txt"))
            {
                Logger.Log("Loadeing hashes...");
                _hashes = File.ReadAllLines($"{_projectRoot}\\hashes.txt")
                    .Where(x => !string.IsNullOrWhiteSpace(x) && x.IndexOf('=') > 0)
                    .Select(x => x.Split('=', 2))
                    .ToDictionary(x => x[0], x => ulong.Parse(x[1]));
            }
            else
            {
                _hashes = new Dictionary<string, ulong>();
                Logger.Log("Hashes not found, rebuilding all tables");
            }
        }

        public void ConvertXmlsToDatafile(bool is64Bit)
        {
            // Load datafiles
            if (_data == null)
            {
                Logger.Log($"Loading datafile: '{_datafilePath}'");
                _data = Datafile.ReadFromFile(_datafilePath, false, is64Bit);
            }

            if (_local == null)
            {
                Logger.Log($"Loadeing localfile: '{_localfilePath}'");
                _local = Datafile.ReadFromFile(_localfilePath, false, is64Bit);
            }

            _tables = _data.Tables
                .Concat(_local.Tables)
                .ToDictionary(x => (int)x.Type);

            // Get modified tables
            var modifiedTables = GetModifiedTables();

            if (modifiedTables.Count == 0)
            {
                Logger.Log("No modified tables found");
                return;
            }

            Logger.Log("Found modified tables: " + string.Join(", ", modifiedTables.Select(x => x.Name)));

            // Resolve aliases
            if (_resolvedAliases == null)
            {
                Logger.Log("Resolving datafile aliases...");
                _resolvedAliases = DatafileAliasResolver.Resolve(_tables.Values, _datafileDef);
            }

            // Resolve modified aliases
            Logger.Log("Resolving modified xml aliases...");
            ResolveModifiedAliases(modifiedTables);

            // Create record builder if it doesn't exist
            _recordBuilder ??= new RecordBuilder(_datafileDef, _resolvedAliases);

            // Process modified tables
            foreach (var modifiedTable in modifiedTables)
            {
                Logger.Log($"Processing table: {modifiedTable.Name}");
                ProcessTable(modifiedTable);
            }

            // Rebuild alias map
            Logger.Log("Rebuilding alias map");
            var rebuilder = _data.NameTable.BeginRebuilding();

            foreach (var table in _tables.Values)
            {
                var tableDef = _datafileDef[table.Type];
                var aliasAttrDef = tableDef.Attributes.FirstOrDefault(x => x.Name == "alias");

                if (aliasAttrDef == null)
                    continue;

                var tablePrefix = tableDef.OriginalName.ToLowerInvariant() + ":";
                var aliasOffset = aliasAttrDef.Offset;

                foreach (var record in table.Records)
                {
                    var alias = record.StringLookup.GetString(record.Get<int>(aliasOffset));
                    rebuilder.AddAliasManually(
                        tablePrefix + alias.ToLowerInvariant(),
                        record.Get<Ref>(8));
                }
            }

            XmlRecordsHelper.LoadAliasesToRebuilder(rebuilder, _extractedXmlDatPath, _extractedLocalDatPath);

            rebuilder.EndRebuilding();
            Logger.Log("Rebuilt alias map");

            // Save modified datafiles
            Logger.Log($"Saving {_datafilePath}");
            _data.WriteToFile(_datafilePath);

            Logger.Log($"Saving {_localfilePath}");
            _local.WriteToFile(_localfilePath);

            // Save hashes
            Logger.Log("Saving hashes...");
            SaveHashes();

            Logger.Log("Done.");
        }

        private void ProcessTable(TableDefinition tableDef)
        {
            // Initialize stuff
            if (!_tables.TryGetValue(tableDef.Type, out var table))
                throw new Exception($"Failed to get original table from datafile: {tableDef.Name}:{tableDef.Type}");

            _recordBuilder.InitializeTable(table.IsCompressed);

            // Clear old records
            table.Records.Clear();

            foreach (var relativePath in GetPathsForTable(tableDef))
            {
                var filePath = $"tables\\{relativePath}.xml";
                var fullFilePath = $"{_projectRoot}\\{filePath}";

                if (!File.Exists(fullFilePath))
                    continue;

                using var xmlReader = new XmlFileReader(fullFilePath, filePath);

                if (!xmlReader.ReadNonComment() || xmlReader.NodeType != XmlNodeType.XmlDeclaration)
                    ThrowHelper.ThrowException("Failed to read xml declaration");

                if (!xmlReader.ReadNonComment() || xmlReader.Name != "table")
                    ThrowHelper.ThrowException("Failed to read table element");

                while (xmlReader.Read())
                {
                    switch (xmlReader.NodeType)
                    {
                        case XmlNodeType.Element:
                            ConvertRecord(table, xmlReader, tableDef);
                            continue;

                        case XmlNodeType.Comment:
                            continue;
                    }

                    break;
                }
            }

            _recordBuilder.FinalizeTable();
        }

        private static List<string> GetPathsForTable(TableDefinition tableDef)
        {
            var pathsToCheck = new List<string>
            {
                $"{tableDef.Name}",
                $"{tableDef.Name}\\base_{tableDef.Name}"
            };

            pathsToCheck.AddRange(tableDef.Subtables.Select(subDef => $"{tableDef.Name}\\{subDef.Name}"));

            return pathsToCheck;
        }

        private void ConvertRecord(Table table, XmlFileReader reader, TableDefinition tableDef)
        {
            _recordBuilder.InitializeRecord();

            // Create record
            var subtableName = reader.GetAttribute("type");
            var def = subtableName == null
                ? (ITableDefinition)tableDef
                : tableDef.SubtableByName(subtableName);

            var record = def.CreateDefaultRecord(_recordBuilder.StringLookup, out var defaultStringAttrsWithValue);
            table.Records.Add(record);

            // Set default strings if needed
            if (defaultStringAttrsWithValue != null)
                _recordBuilder.SetDefaultStrings(record, defaultStringAttrsWithValue);

            // Go through each attribute
            if (reader.MoveToFirstAttribute())
            {
                do
                {
                    if (reader.Name == "type")
                        continue;

                    var attrDef = def.ExpandedAttributeByName(reader.Name);

                    if (attrDef != null)
                        _recordBuilder.SetAttribute(record, attrDef, reader.Value);
                    else
                        reader.Log($"Failed to find attrDef for: {reader.Name}, value: {reader.Value}");
                } while (reader.MoveToNextAttribute());
            }

            _recordBuilder.FinalizeRecord();
        }

        private void ResolveModifiedAliases(HashSet<TableDefinition> modifiedTables)
        {
            var xmlAliases = XmlAliasResolver.Resolve(_projectRoot + "\\tables", modifiedTables, GetPathsForTable);

            foreach (var (id, byAlias) in xmlAliases.ByAlias)
                _resolvedAliases.ByAlias[id] = byAlias;

            foreach (var (id, byRef) in xmlAliases.ByRef)
                _resolvedAliases.ByRef[id] = byRef;

            ExtractedXmlDatAliasResolver.Resolve(_resolvedAliases, _datafileDef, _extractedXmlDatPath, _extractedLocalDatPath);
        }

        private HashSet<TableDefinition> GetModifiedTables()
        {
            _modifiedHashes.Clear();

            var modifiedTables = new HashSet<TableDefinition>();

            foreach (var tableDef in _datafileDef.TableDefinitions)
            {
                foreach (var name in GetPathsForTable(tableDef))
                {
                    var path = $"{_projectRoot}\\tables\\{name}.xml";

                    if (!File.Exists(path))
                        continue;

                    var hasHash = _hashes.TryGetValue(name, out var originalHash);

                    // Read xml into memory
                    _buffer.SetLength(0);

                    using var fileStream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);

                    fileStream.CopyTo(_buffer);

                    // Hash the xml file
                    Span<byte> data = _buffer.GetBuffer()[..(int)_buffer.Length];
                    var hash = XXH64.DigestOf(data);

                    // Check if hash changed
                    if (!hasHash || hash != originalHash)
                    {
                        modifiedTables.Add(tableDef);
                        _modifiedHashes[name] = hash;
                    }
                }
            }

            return modifiedTables;
        }

        private void SaveHashes()
        {
            foreach (var (key, hash) in _modifiedHashes)
                _hashes[key] = hash;

            File.WriteAllLines(_projectRoot + "\\hashes.txt",
                _hashes.OrderBy(x => x.Key).Select(x => $"{x.Key}={x.Value}"));

            _modifiedHashes.Clear();
        }
    }
}