using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace gated.Network;

public static class Arrays
{
    public static double CalcSum(double[] values) => values.Sum();

    public static double CalcSum(double[] values, int begin_index, int end_index)
    {
        var sum = 0.0;
        for (var i = begin_index; i < end_index; i++)
            sum += values[i];
        return sum;
    }

    public static double CalcAverage(double[] values) => CalcSum(values) / values.Length;

    public static double CalcMedian(double[] values)
    {
        var sorted = (double[])values.Clone();
        System.Array.Sort(sorted);
        return sorted.Length % 2 == 0
            ? (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2
            : sorted[sorted.Length / 2];
    }

    public static double CalcMinimum(double[] values) => values.Min();
    public static double CalcMaximum(double[] values) => values.Max();
    public static int CalcMinimum(int[] values) => values.Min();
    public static int CalcMaximum(int[] values) => values.Max();

    public static double[] CreateDoubleArrayOfOnes(int element_count) => Repeat(1.0, element_count);
    public static int[] Repeat(int value, int element_count) => Enumerable.Repeat(value, element_count).ToArray();
    public static double[] Repeat(double value, int element_count) => Enumerable.Repeat(value, element_count).ToArray();

    public static double[] CreateDoubleArrayOfRandomNumbers(int element_count) => CreateDoubleArrayOfRandomNumbers(element_count, new Random());

    public static double[] CreateDoubleArrayOfRandomNumbers(int element_count, Random random)
    {
        var values = new double[element_count];
        for (var i = 0; i < values.Length; i++)
            values[i] = random.NextDouble();
        return values;
    }

    public static int[] GenerateRandomPermutation(int element_count) => GenerateRandomPermutation(element_count, new Random());

    public static int[] GenerateRandomPermutation(int element_count, Random random)
    {
        var permutation = Enumerable.Range(0, element_count).ToArray();
        PermuteRandomly(permutation, random);
        return permutation;
    }

    public static void PermuteRandomly(int[] elements, Random random)
    {
        for (var i = elements.Length - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (elements[i], elements[j]) = (elements[j], elements[i]);
        }
    }
}

