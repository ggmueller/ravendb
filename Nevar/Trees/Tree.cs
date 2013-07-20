﻿using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.IO;
using Nevar.Impl;

namespace Nevar.Trees
{
	public unsafe class Tree
	{
		public int BranchPages;
		public int LeafPages;
		public int OverflowPages;
		public int Depth;
		public int PageCount;

		private readonly SliceComparer _cmp;

		public Page Root;

		private Tree(SliceComparer cmp, Page root)
		{
			_cmp = cmp;
			Root = root;
		}

		public static Tree CreateOrOpen(Transaction tx, int root, SliceComparer cmp)
		{
			if (root != -1)
			{
				return new Tree(cmp, tx.GetPage(root));
			}

			// need to create the root
			var newRootPage = NewPage(tx, PageFlags.Leaf, 1);
			var tree = new Tree(cmp, newRootPage) { Depth = 1 };
			var cursor = tx.GetCursor(tree);
			cursor.RecordNewPage(newRootPage, 1);
			return tree;
		}

		public void Add(Transaction tx, Slice key, Stream value)
		{
			if (value == null) throw new ArgumentNullException("value");
			if (value.Length > int.MaxValue) throw new ArgumentException("Cannot add a value that is over 2GB in size", "value");

			var cursor = tx.GetCursor(this);

			var page = FindPageFor(tx, key, cursor);

			if (page.LastMatch == 0) // this is an update operation
			{
				RemoveLeafNode(tx, page);
			}

			var pageNumber = -1;
			if (ShouldGoToOverflowPage(value))
			{
				pageNumber = WriteToOverflowPages(tx, cursor, value);
				value = null;
			}

			if (page.HasSpaceFor(key, value) == false)
			{
				new PageSplitter(tx, _cmp, key, value, pageNumber, cursor).Execute();
				DebugValidateTree(tx, cursor.Root);
				return;
			}

			page.AddNode(page.LastSearchPosition, key, value, pageNumber);

			page.DebugValidate(_cmp);
		}

		private static int WriteToOverflowPages(Transaction tx, Cursor cursor, Stream value)
		{
			int numberOfPages = (int) ((Constants.PageHeaderSize - 1) + value.Length)/(Constants.PageSize + 1);
			var overflowPageStart = tx.AllocatePage(numberOfPages);
			overflowPageStart.NumberOfPages = numberOfPages;
			overflowPageStart.Flags = PageFlags.Overlfow;

			using (var ums = new UnmanagedMemoryStream(overflowPageStart.Base + Constants.PageHeaderSize, 
				value.Length, value.Length, FileAccess.ReadWrite))
			{
				value.CopyTo(ums);
			}
			cursor.OverflowPages += numberOfPages;
			cursor.PageCount += numberOfPages;
			return overflowPageStart.PageNumber;
		}

		private bool ShouldGoToOverflowPage(Stream value)
		{
			return value.Length + Constants.PageHeaderSize > Constants.MaxNodeSize;
		}

		private static void RemoveLeafNode(Transaction tx, Page page)
		{
			var node = page.GetNode(page.LastSearchPosition);
			if (node->Flags.HasFlag(NodeFlags.PageRef)) // this is an overflow pointer
			{
				var overflowPage = tx.GetPage(node->PageNumber);
				for (int i = 0; i < overflowPage.NumberOfPages; i++)
				{
					tx.FreePage(tx.GetPage(node->PageNumber + i));
				}
			}
			page.RemoveNode(page.LastSearchPosition);
		}

		[Conditional("DEBUG")]
		private void DebugValidateTree(Transaction tx, Page root)
		{
			var stack = new Stack<Page>();
			stack.Push(root);
			while (stack.Count > 0)
			{
				var p = stack.Pop();
				p.DebugValidate(_cmp);
				if (p.IsBranch == false)
					continue;
				for (int i = 0; i < p.NumberOfEntries; i++)
				{
					stack.Push(tx.GetPage(p.GetNode(i)->PageNumber));
				}
			}
		}



		public Page FindPageFor(Transaction tx, Slice key, Cursor cursor)
		{
			var p = cursor.Root;
			cursor.Push(p);
			while (p.Flags.HasFlag(PageFlags.Branch))
			{
				int nodePos;
				if (key.Options == SliceOptions.BeforeAllKeys)
				{
					nodePos = 0;
				}
				else if (key.Options == SliceOptions.AfterAllKeys)
				{
					nodePos = (ushort)(p.NumberOfEntries - 1);
				}
				else
				{
					if (p.Search(key, _cmp) != null)
					{
						nodePos = p.LastSearchPosition;
						if (p.LastMatch != 0)
						{
							nodePos--;
							p.LastSearchPosition--;
						}
					}
					else
					{
						nodePos = (ushort)(p.LastSearchPosition - 1);
					}

				}

				var node = p.GetNode(nodePos);
				p = tx.GetPage(node->PageNumber);
				cursor.Push(p);
			}

			if (p.IsLeaf == false)
				throw new DataException("Index points to a non leaf page");

			p.NodePositionFor(key, _cmp); // will set the LastSearchPosition

			return p;
		}

		internal static Page NewPage(Transaction tx, PageFlags flags, int num)
		{
			var page = tx.AllocatePage(num);

			page.Flags = flags;

			return page;
		}

		public void Delete(Transaction tx, Slice key)
		{
			var cursor = tx.GetCursor(this);

			var page = FindPageFor(tx, key, cursor);

			page.NodePositionFor(key, _cmp);
			if (page.LastMatch != 0)
				return; // not an exact match, can't delete
			RemoveLeafNode(tx, page);

			var treeRebalancer = new TreeRebalancer(tx);

			var changedPage = page;
			while (changedPage != null)
			{
				changedPage = treeRebalancer.Execute(cursor, changedPage);
			}

			page.DebugValidate(_cmp);
		}

		public List<Slice> KeysAsList(Transaction tx)
		{
			var l = new List<Slice>();
			AddKeysToListInOrder(tx, l, Root);
			return l;
		}

		private void AddKeysToListInOrder(Transaction tx, List<Slice> l, Page page)
		{
			for (int i = 0; i < page.NumberOfEntries; i++)
			{
				var node = page.GetNode(i);
				if (page.IsBranch)
				{
					var p = tx.GetPage(node->PageNumber);
					AddKeysToListInOrder(tx, l, p);
				}
				else
				{
					l.Add(new Slice(node));
				}
			}
		}
	}
}