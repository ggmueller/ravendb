﻿using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using Nevar.Impl;

namespace Nevar.Trees
{
	public unsafe class Page
	{
		private readonly byte* _base;
		private readonly PageHeader* _header;
		public int LastMatch;

		public Page(byte* b)
		{
			_base = b;
			_header = (PageHeader*)b;
		}

		public int PageNumber { get { return _header->PageNumber; } set { _header->PageNumber = value; } }

		public PageFlags Flags { get { return _header->Flags; } set { _header->Flags = value; } }

		public ushort Lower { get { return _header->Lower; } set { _header->Lower = value; } }

		public ushort Upper { get { return _header->Upper; } set { _header->Upper = value; } }

		public int NumberOfPages { get { return _header->NumberOfPages; } set { _header->NumberOfPages = value; } }

		public ushort* KeysOffsets
		{
			get { return (ushort*)(_base + Constants.PageHeaderSize); }
		}

		public NodeHeader* Search(Slice key, SliceComparer cmp)
		{
			if (NumberOfEntries == 0)
			{
				LastSearchPosition = 0;
				LastMatch = 1;
				return null;
			}

			if (key.Options == SliceOptions.BeforeAllKeys)
			{
				LastSearchPosition = 0;
				LastMatch = 1;
				return GetNode(0);
			}

			if (key.Options == SliceOptions.AfterAllKeys)
			{
				LastMatch = -1;
				LastSearchPosition = NumberOfEntries - 1;
				return GetNode(LastSearchPosition);
			}

			int low = IsLeaf ? 0 : 1;
			int high = NumberOfEntries - 1;
			int position = 0;

			var pageKey = new Slice(SliceOptions.Key);
			bool matched = false;
			NodeHeader* node = null;
			while (low <= high)
			{
				position = (low + high) >> 1;

				node = GetNode(position);
				pageKey.Set(node);

				LastMatch = key.Compare(pageKey, cmp);
				matched = true;
				if (LastMatch == 0)
					break;

				if (LastMatch > 0)
					low = position + 1;
				else
					high = position - 1;
			}

			if (matched == false)
			{
				node = GetNode(position);
				LastMatch = key.Compare(pageKey, cmp);
			}

			if (LastMatch > 0) // found entry less than key
				position++; // move to the smallest entry larger than the key

			System.Diagnostics.Debug.Assert(position < ushort.MaxValue);
			LastSearchPosition = position;

			if (position >= NumberOfEntries)
				return null;
			return node;
		}

		public NodeHeader* GetNode(int n)
		{
			System.Diagnostics.Debug.Assert(n >= 0 && n <= NumberOfEntries);

			var nodeOffset = KeysOffsets[n];
			var nodeHeader = (NodeHeader*)(_base + nodeOffset);

			return nodeHeader;
		}


		public bool IsLeaf
		{
			get { return _header->Flags.HasFlag(PageFlags.Leaf); }
		}

		public bool IsBranch
		{
			get { return _header->Flags.HasFlag(PageFlags.Branch); }
		}

		public ushort NumberOfEntries
		{
			get
			{
				// Because we store the keys offset from the end of the head to lower
				// we can calculate the number of entries by getting the size and dividing
				// in 2, since that is the size of the offsets we use

				return (ushort)((_header->Lower - Constants.PageHeaderSize) >> 1);
			}
		}

		public void RemoveNode(int index)
		{
			System.Diagnostics.Debug.Assert(index < NumberOfEntries);

			var node = GetNode(index);

			var size = SizeOf.NodeEntry(node);

			var nodeOffset = KeysOffsets[index];

			int modifiedEntries = 0;
			for (int i = 0; i < NumberOfEntries; i++)
			{
				if (i == index)
					continue;
				KeysOffsets[modifiedEntries] = KeysOffsets[i];
				if (KeysOffsets[i] < nodeOffset)
					KeysOffsets[modifiedEntries] += (ushort)size;
				modifiedEntries++;
			}

			NativeMethods.memmove(_base + Upper + size, _base + Upper, nodeOffset - Upper);

			Lower -= (ushort)Constants.NodeOffsetSize;
			Upper += (ushort)size;

		}

		public void AddNode(int index, Slice key, Stream value, int pageNumber)
		{
			if (HasSpaceFor(key, value) == false)
				throw new InvalidOperationException("The page is full and cannot add an entry, this is probably a bug");

			// move higher pointers up one slot
			for (int i = NumberOfEntries; i > index; i--)
			{
				KeysOffsets[i] = KeysOffsets[i - 1];
			}
			var nodeSize = SizeOf.NodeEntry(key, value);
			var node = AllocateNewNode(index, key, nodeSize);

			if (key.Options == SliceOptions.Key)
				key.CopyTo((byte*)node + Constants.NodeHeaderSize);

			if (value == null) // branch or overflow
			{
				Debug.Assert(pageNumber != -1);
				node->PageNumber = pageNumber;
				node->Flags = NodeFlags.PageRef;
				return;
			}

			Debug.Assert(key.Options == SliceOptions.Key);
			Debug.Assert(value != null);
			var dataPos = (byte*)node + Constants.NodeHeaderSize + key.Size;
			node->DataSize = (int)value.Length;
			node->Flags = NodeFlags.Data;
			using (var ums = new UnmanagedMemoryStream(dataPos, value.Length, value.Length, FileAccess.ReadWrite))
			{
				value.CopyTo(ums);
			}
		}

		/// <summary>
		/// Internal method that is used when splitting pages
		/// No need to do any work here, we are always adding at the end
		/// </summary>
		internal void CopyNodeDataToEndOfPage(NodeHeader* other, Slice key = null)
		{
			System.Diagnostics.Debug.Assert(SizeOf.NodeEntry(other) + Constants.NodeOffsetSize <= SizeLeft);

			var index = NumberOfEntries;

			var nodeSize = SizeOf.NodeEntry(other);

			key = key ?? new Slice(other);
			var newNode = AllocateNewNode(index, key, nodeSize);
			newNode->Flags = other->Flags;
			key.CopyTo((byte*)newNode + Constants.NodeHeaderSize);

			if (IsBranch)
			{
				newNode->PageNumber = other->PageNumber;
				newNode->Flags = NodeFlags.PageRef;
				return;
			}
			newNode->DataSize = other->DataSize;
			NativeMethods.memcpy((byte*)newNode + Constants.NodeHeaderSize + other->KeySize,
								 (byte*)other + Constants.NodeHeaderSize + other->KeySize,
								 other->DataSize);
		}


		private NodeHeader* AllocateNewNode(int index, Slice key, int nodeSize)
		{
			var newNodeOffset = (ushort)(_header->Upper - nodeSize);
			System.Diagnostics.Debug.Assert(newNodeOffset >= _header->Lower + Constants.NodeOffsetSize);
			KeysOffsets[index] = newNodeOffset;
			_header->Upper = newNodeOffset;
			_header->Lower += (ushort)Constants.NodeOffsetSize;

			var node = (NodeHeader*)(_base + newNodeOffset);
			node->KeySize = key.Size;
			node->Flags = NodeFlags.None;
			return node;
		}


		public int SizeLeft
		{
			get { return _header->Upper - _header->Lower; }
		}

		public int SizeUsed
		{
			get { return _header->Lower + Constants.PageMaxSpace - _header->Upper; }
		}

		public int LastSearchPosition { get; set; }

		public byte* Base
		{
			get { return _base; }
		}

		public void Truncate(Transaction tx, int i)
		{
			if (i >= NumberOfEntries)
				return;

			// when truncating, we copy the values to a tmp page
			// this has the effect of compacting the page data and avoiding
			// internal page fragmentation
			var copy = tx.AllocatePage(1);
			copy.Flags = Flags;
			for (int j = 0; j < i; j++)
			{
				copy.CopyNodeDataToEndOfPage(GetNode(j));
			}
			NativeMethods.memcpy(_base + Constants.PageHeaderSize,
								 copy._base + Constants.PageHeaderSize,
								 Constants.PageSize - Constants.PageHeaderSize);

			Upper = copy.Upper;
			Lower = copy.Lower;
			tx.FreePage(copy);

			if (LastSearchPosition > i)
				LastSearchPosition = i;
		}

		public int NodePositionFor(Slice key, SliceComparer cmp)
		{
			Search(key, cmp);
			return LastSearchPosition;
		}

		public override string ToString()
		{
			return "#" + PageNumber + " (count: " + NumberOfEntries + ") " + Flags;
		}

		public string Dump()
		{
			var sb = new StringBuilder();
			var slice = new Slice(SliceOptions.Key);
			for (var i = 0; i < NumberOfEntries; i++)
			{
				var n = GetNode(i);
				slice.Set(n);
				sb.Append(slice).Append(", ");
			}
			return sb.ToString();
		}

		public bool HasSpaceFor(Slice key, Stream value)
		{
			var requiredSpace = GetRequiredSpace(key, value);
			return requiredSpace <= SizeLeft;
		}

		public static int GetRequiredSpace(Slice key, Stream value)
		{
			return SizeOf.NodeEntry(key, value) + Constants.NodeOffsetSize;
		}

		public string this[int i]
		{
			get { return new Slice(GetNode(i)).ToString(); }
		}

		[Conditional("DEBUG")]
		public void DebugValidate(SliceComparer comparer)
		{
			if (NumberOfEntries == 0)
				return;

			var prev = new Slice(GetNode(0));
			for (int i = 1; i < NumberOfEntries; i++)
			{
				var node = GetNode(i);
				var current = new Slice(node);

				if (prev.Compare(current, comparer) >= 0)
					throw new InvalidOperationException("The page " + PageNumber + " is not sorted");

				prev = current;
			}
		}
	}
}