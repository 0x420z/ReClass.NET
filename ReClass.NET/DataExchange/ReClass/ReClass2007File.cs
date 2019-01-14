﻿using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SQLite;
using System.Diagnostics.Contracts;
using System.Linq;
using ReClassNET.DataExchange.ReClass.Legacy;
using ReClassNET.Extensions;
using ReClassNET.Logger;
using ReClassNET.Nodes;

namespace ReClassNET.DataExchange.ReClass
{
	public class ReClass2007File : IReClassImport
	{
		public const string FormatName = "ReClass 2007 File";
		public const string FileExtension = ".rdc";

		private static readonly Type[] typeMap =
		{
			null,
			typeof(ClassInstanceNode),
			typeof(ClassNode),
			null,
			typeof(Hex32Node),
			typeof(Hex16Node),
			typeof(Hex8Node),
			typeof(ClassPointerNode),
			typeof(Int32Node),
			typeof(Int16Node),
			typeof(Int8Node),
			typeof(FloatNode),
			typeof(UInt32Node),
			typeof(UInt16Node),
			typeof(UInt8Node),
			typeof(Utf8TextNode),
			typeof(FunctionPtrNode)
		};

		private readonly ReClassNetProject project;

		public ReClass2007File(ReClassNetProject project)
		{
			Contract.Requires(project != null);

			this.project = project;
		}

		public void Load(string filePath, ILogger logger)
		{
			using (var connection = new SQLiteConnection($"Data Source={filePath}"))
			{
				connection.Open();

				var classes = new Dictionary<int, ClassNode>();
				var vtables = new Dictionary<int, VTableNode>();

				foreach (var row in Query(connection, "SELECT tbl_name FROM sqlite_master WHERE tbl_name LIKE 'class%'"))
				{
					var id = Convert.ToInt32(row["tbl_name"].ToString().Substring(5));

					var classRow = Query(connection, $"SELECT variable, comment FROM class{id} WHERE type = 2 LIMIT 1").FirstOrDefault();
					if (classRow == null)
					{
						continue;
					}

					// Skip the vtable classes.
					if (classRow["variable"].ToString() == "VTABLE")
					{
						var vtableNode = new VTableNode();

						Query(connection, $"SELECT variable, comment FROM class{id} WHERE type = 16")
							.Select(e => new VMethodNode
							{
								Name = Convert.ToString(e["variable"]),
								Comment = Convert.ToString(e["comment"])
							})
							.ForEach(vtableNode.AddNode);

						foreach (var method in vtableNode.Nodes.Where(m => m.Name == "void function()"))
						{
							method.Name = string.Empty;
						}

						vtables.Add(id, vtableNode);

						continue;
					}

					var node = new ClassNode(false)
					{
						Name = classRow["variable"].ToString(),
						Comment = classRow["comment"].ToString()
					};

					project.AddClass(node);

					classes.Add(id, node);
				}

				foreach (var kv in classes)
				{
					ReadNodeRows(
						Query(connection, $"SELECT variable, comment, type, length, ref FROM class{kv.Key} WHERE type != 2"),
						kv.Value,
						classes,
						vtables,
						logger
					).ForEach(kv.Value.AddNode);
				}
			}
		}

		private static IEnumerable<BaseNode> ReadNodeRows(IEnumerable<DataRow> rows, ClassNode parent, IReadOnlyDictionary<int, ClassNode> classes, IReadOnlyDictionary<int, VTableNode> vtables, ILogger logger)
		{
			Contract.Requires(rows != null);
			Contract.Requires(parent != null);
			Contract.Requires(logger != null);

			foreach (var row in rows)
			{
				Type nodeType = null;

				var typeVal = Convert.ToInt32(row["type"]);
				if (typeVal >= 0 && typeVal < typeMap.Length)
				{
					nodeType = typeMap[typeVal];
				}

				if (nodeType == null)
				{
					logger.Log(LogLevel.Error, $"Skipping node with unknown type: {row["type"]}");
					logger.Log(LogLevel.Warning, string.Join(",", row.ItemArray));

					continue;
				}

				var node = BaseNode.CreateInstanceFromType(nodeType);
				if (node == null)
				{
					logger.Log(LogLevel.Error, $"Could not create node of type: {nodeType}");

					continue;
				}

				node.Name = Convert.ToString(row["variable"]);
				node.Comment = Convert.ToString(row["comment"]);

				// ClassInstanceNode, ClassPointerNode
				if (node is BaseWrapperNode wrapperNode)
				{
					var reference = Convert.ToInt32(row["ref"]);
					if (!classes.ContainsKey(reference))
					{
						if (!vtables.TryGetValue(reference, out var vtableNode))
						{
							logger.Log(LogLevel.Error, $"Skipping node with unknown reference: {row["ref"]}");
							logger.Log(LogLevel.Warning, string.Join(",", row.ItemArray));

							continue;
						}

						yield return vtableNode;

						continue;
					}

					var innerClassNode = classes[reference];
					if (wrapperNode.PerformCycleCheck && !ClassUtil.IsCyclicIfClassIsAccessibleFromParent(parent, innerClassNode, classes.Values))
					{
						logger.Log(LogLevel.Error, $"Skipping node with cycle reference: {parent.Name}->{node.Name}");

						continue;
					}

					if (node is ClassPointerNode classPointerNode)
					{
						node = classPointerNode.GetEquivalentNode(innerClassNode);
					}
					else
					{
						wrapperNode.ChangeInnerNode(innerClassNode);
					}
				}
				if (node is BaseTextNode textNode)
				{
					textNode.Length = Math.Max(IntPtr.Size, Convert.ToInt32(row["length"]));
				}

				yield return node;
			}
		}

		private static IEnumerable<DataRow> Query(SQLiteConnection connection, string query)
		{
			Contract.Requires(connection != null);
			Contract.Requires(query != null);
			Contract.Ensures(Contract.Result<IEnumerable<DataRow>>() != null);

			using (var adapter = new SQLiteDataAdapter(query, connection))
			{
				var ds = new DataSet();

				adapter.Fill(ds);

				return ds.Tables[0].AsEnumerable();
			}
		}
	}
}
