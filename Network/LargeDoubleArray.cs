using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace gated.Network;

public sealed class LargeDoubleArray : IEnumerable<double>, ICloneable
{
    private double[] values;
    private long element_count;
    public LargeDoubleArray(long element_count) : this(element_count, 0) { }
    public LargeDoubleArray(long element_count, double constant) { values = new double[CheckedLength(element_count)]; this.element_count = element_count; if (constant != 0) System.Array.Fill(values, constant); }
    public LargeDoubleArray(double[] values) { this.values = (double[])values.Clone(); element_count = values.LongLength; }
    public static int GetSegment(long index) => 0;
    public static int GetOffset(long index) => CheckedLength(index);
    public int NSegments() => 1;
    public int Length(int segment) => CheckedLength(element_count);
    public double Get(long index) => values[CheckedLength(index)];
    public double Get(int segment, int offset) => values[offset];
    public void Set(long index, double value) => values[CheckedLength(index)] = value;
    public void Set(int segment, int offset, double value) => values[offset] = value;
    public void Fill(double constant) => System.Array.Fill(values, constant, 0, CheckedLength(element_count));
    public void Fill(long from_index, long to, double constant) => System.Array.Fill(values, constant, CheckedLength(from_index), CheckedLength(to - from_index));
    public void Append(double value) => Push(value);
    public void Push(double value) { EnsureCapacity(element_count + 1); values[CheckedLength(element_count++)] = value; }
    public double Pop() => values[CheckedLength(--element_count)];
    public void Clear() => element_count = 0;
    public void EnsureCapacity(long minimum_capacity) { if (minimum_capacity > values.LongLength) System.Array.Resize(ref values, CheckedLength(Math.Max(minimum_capacity, values.LongLength * 2 + 1))); }
    public void Resize(long element_count) { EnsureCapacity(element_count); this.element_count = element_count; }
    public void Shrink() => System.Array.Resize(ref values, CheckedLength(element_count));
    public long Size() => element_count;
    public long Capacity() => values.LongLength;
    public void Add(long index, double addition) => values[CheckedLength(index)] += addition;
    public void Subtract(long index, double subtraction) => values[CheckedLength(index)] -= subtraction;
    public void Multiply(long index, double multiplier) => values[CheckedLength(index)] *= multiplier;
    public void Divide(long index, double divisor) => values[CheckedLength(index)] /= divisor;
    public double CalcSum() => CalcSum(0, element_count);
    public double CalcSum(long from_index, long to) { var sum = 0.0; for (var i = from_index; i < to; i++) sum += Get(i); return sum; }
    public double CalcAverage() => CalcSum() / element_count;
    public double CalcMaximum() => ToArray().Max();
    public double CalcMinimum() => ToArray().Min();
    public void Swap(long index_a, long index_b) => (values[CheckedLength(index_a)], values[CheckedLength(index_b)]) = (values[CheckedLength(index_b)], values[CheckedLength(index_a)]);
    public void MergeSort() => Sort();
    public void QuickSort() => Sort();
    public void Sort() => System.Array.Sort(values, 0, CheckedLength(element_count));
    public void Sort(LongComparator comparator) => SortByComparator(comparator);
    public long BinarySearch(double value) => BinarySearch(0, element_count, value);
    public long BinarySearch(long from_index, long to, double value) { var result = System.Array.BinarySearch(values, CheckedLength(from_index), CheckedLength(to - from_index), value); return result >= 0 ? result : result; }
    public FromIterable From(long from_index) => new(this, from_index);
    public FromToIterable FromTo(long from_index, long to) => new(this, from_index, to);
    public void UpdateFrom(LargeDoubleArray array) => UpdateFrom(array, 0, array.element_count, 0);
    public void UpdateFrom(LargeDoubleArray array, long from_index, long to, long insertion_point) { EnsureCapacity(insertion_point + to - from_index); System.Array.Copy(array.values, CheckedLength(from_index), values, CheckedLength(insertion_point), CheckedLength(to - from_index)); element_count = Math.Max(element_count, insertion_point + to - from_index); }
    public LargeDoubleArray CopyOfRange(long from_index, long to) => new(ToArray(from_index, to));
    public double[] ToArray() => ToArray(0, element_count);
    public double[] ToArray(long from_index, long to) { var result = new double[CheckedLength(to - from_index)]; System.Array.Copy(values, CheckedLength(from_index), result, 0, result.Length); return result; }
    public LargeDoubleArray Clone() => new(ToArray());
    object ICloneable.Clone() => Clone();
    public IEnumerator<double> GetEnumerator() => FromTo(0, element_count).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    private void SortByComparator(LongComparator comparator) { var idx = Enumerable.Range(0, CheckedLength(element_count)).Select(i => (long)i).ToArray(); System.Array.Sort(idx, (a, b) => comparator(a, b)); var copy = (double[])values.Clone(); for (var i = 0; i < idx.Length; i++) values[i] = copy[CheckedLength(idx[i])]; }
    private static int CheckedLength(long value) => value > int.MaxValue ? throw new ArgumentOutOfRangeException(nameof(value), "This C# port stores large arrays in managed arrays and is limited to Int32.MaxValue elements.") : (int)value;
    public sealed class FromIterable : IEnumerable<double> { private readonly LargeDoubleArray array; private readonly long from_index; public FromIterable(LargeDoubleArray array, long from_index) { this.array = array; this.from_index = from_index; } public IEnumerator<double> GetEnumerator() => new FromToIterable(array, from_index, array.element_count).GetEnumerator(); IEnumerator IEnumerable.GetEnumerator() => GetEnumerator(); }
    public sealed class FromToIterable : IEnumerable<double> { private readonly LargeDoubleArray array; private readonly long from_index; private readonly long to; public FromToIterable(LargeDoubleArray array, long from_index, long to) { this.array = array; this.from_index = from_index; this.to = to; } public IEnumerator<double> GetEnumerator() { for (var i = from_index; i < to; i++) yield return array.Get(i); } IEnumerator IEnumerable.GetEnumerator() => GetEnumerator(); }
}

