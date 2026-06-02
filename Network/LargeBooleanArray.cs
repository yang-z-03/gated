using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace gated.Network;

public sealed class LargeBooleanArray : IEnumerable<bool>, ICloneable
{
    private bool[] values;
    private long element_count;

    public LargeBooleanArray(long element_count) : this(element_count, false) { }

    public LargeBooleanArray(long element_count, bool constant)
    {
        values = new bool[CheckedLength(element_count)];
        this.element_count = element_count;
        if (constant)
            System.Array.Fill(values, true);
    }

    public LargeBooleanArray(bool[] values)
    {
        this.values = (bool[])values.Clone();
        element_count = values.LongLength;
    }

    public static int GetSegment(long index) => 0;
    public static int GetOffset(long index) => CheckedLength(index);
    public int NSegments() => 1;
    public int Length(int segment) => CheckedLength(element_count);
    public bool Get(long index) => values[CheckedLength(index)];
    public bool Get(int segment, int offset) => values[offset];
    public void Set(long index, bool value) => values[CheckedLength(index)] = value;
    public void Set(int segment, int offset, bool value) => values[offset] = value;
    public void Fill(bool constant) => System.Array.Fill(values, constant, 0, CheckedLength(element_count));
    public void Fill(long from_index, long to, bool constant) => System.Array.Fill(values, constant, CheckedLength(from_index), CheckedLength(to - from_index));
    public void Append(bool value) => Push(value);
    public void Push(bool value) { EnsureCapacity(element_count + 1); values[CheckedLength(element_count++)] = value; }
    public bool Pop() => values[CheckedLength(--element_count)];
    public void Clear() => element_count = 0;
    public void EnsureCapacity(long minimum_capacity) { if (minimum_capacity > values.LongLength) System.Array.Resize(ref values, CheckedLength(Math.Max(minimum_capacity, values.LongLength * 2 + 1))); }
    public void Resize(long element_count) { EnsureCapacity(element_count); this.element_count = element_count; }
    public void Shrink() => System.Array.Resize(ref values, CheckedLength(element_count));
    public long Size() => element_count;
    public long Capacity() => values.LongLength;
    public void Swap(long index_a, long index_b) => (values[CheckedLength(index_a)], values[CheckedLength(index_b)]) = (values[CheckedLength(index_b)], values[CheckedLength(index_a)]);
    public void MergeSort() => Sort();
    public void QuickSort() => Sort();
    public void Sort() => System.Array.Sort(values, 0, CheckedLength(element_count));
    public void Sort(LongComparator comparator) => SortByComparator(comparator);
    public void UpdateFrom(LargeBooleanArray array) => UpdateFrom(array, 0, array.element_count, 0);
    public void UpdateFrom(LargeBooleanArray array, long from_index, long to, long insertion_point) { EnsureCapacity(insertion_point + to - from_index); System.Array.Copy(array.values, CheckedLength(from_index), values, CheckedLength(insertion_point), CheckedLength(to - from_index)); element_count = Math.Max(element_count, insertion_point + to - from_index); }
    public LargeBooleanArray CopyOfRange(long from_index, long to) => new(ToArray(from_index, to));
    public bool[] ToArray() => ToArray(0, element_count);
    public bool[] ToArray(long from_index, long to) { var result = new bool[CheckedLength(to - from_index)]; System.Array.Copy(values, CheckedLength(from_index), result, 0, result.Length); return result; }
    public LargeBooleanArray Clone() => new(ToArray());
    object ICloneable.Clone() => Clone();
    public IEnumerator<bool> GetEnumerator() { for (var i = 0L; i < element_count; i++) yield return values[CheckedLength(i)]; }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    private void SortByComparator(LongComparator comparator) { var slice = ToArray(); var idx = Enumerable.Range(0, slice.Length).Select(i => (long)i).ToArray(); System.Array.Sort(idx, (a, b) => comparator(a, b)); var copy = (bool[])values.Clone(); for (var i = 0; i < idx.Length; i++) values[i] = copy[CheckedLength(idx[i])]; }
    private static int CheckedLength(long value) => value > int.MaxValue ? throw new ArgumentOutOfRangeException(nameof(value), "This C# port stores large arrays in managed arrays and is limited to Int32.MaxValue elements.") : (int)value;
}

