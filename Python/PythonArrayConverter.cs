using System;
using System.Collections.Generic;
using System.Linq;
using gated.Models;
using Python.Runtime;

namespace gated.Python;

public static class PythonArrayConverter
{
    public static PyObject ToNumpy(float[,] matrix)
    {
        using (Py.GIL())
        {
            using var rows = new PyList();
            int row_count = matrix.GetLength(0);
            int column_count = matrix.GetLength(1);
            for (int row = 0; row < row_count; row++)
            {
                using var values = new PyList();
                for (int column = 0; column < column_count; column++)
                    values.Append(new PyFloat(matrix[row, column]));
                rows.Append(values);
            }

            dynamic numpy = Py.Import("numpy");
            return numpy.array(rows);
        }
    }

    public static PyObject ToNumpy(double[] values)
    {
        using (Py.GIL())
        {
            using var list = new PyList();
            foreach (double value in values)
                list.Append(new PyFloat(value));

            dynamic numpy = Py.Import("numpy");
            return numpy.array(list);
        }
    }

    public static PyObject ToNumpy(bool[] values)
    {
        using (Py.GIL())
        {
            using var list = new PyList();
            foreach (bool value in values)
                list.Append(value.ToPython());

            dynamic numpy = Py.Import("numpy");
            return numpy.array(list);
        }
    }

    public static PyObject ToNumpy(float[] values)
    {
        using (Py.GIL())
        {
            using var list = new PyList();
            foreach (float value in values)
                list.Append(new PyFloat(value));

            dynamic numpy = Py.Import("numpy");
            return numpy.array(list);
        }
    }

    public static float[,] ToFloatMatrix(PyObject value)
    {
        using (Py.GIL())
        {
            dynamic numpy = Py.Import("numpy");
            using PyObject array = numpy.asarray(value);
            using PyObject list = array.InvokeMethod("tolist");
            var rows = new PyList(list);
            long row_count = rows.Length();
            long column_count = row_count == 0 ? 0 : new PyList(rows[0]).Length();
            var matrix = new float[row_count, column_count];
            for (int row = 0; row < row_count; row++)
            {
                var r = new PyList(rows[row]);
                if (r.Length() != column_count)
                    throw new ArgumentException("Matrix rows must have consistent dimensions.");
                for (int column = 0; column < column_count; column++)
                    matrix[row, column] = r[column].As<float>();
            }

            return matrix;
        }
    }

    public static double[] ToDoubleArray(PyObject? value)
    {
        using (Py.GIL())
        {
            if (value is null || value.IsNone())
                return [];

            dynamic numpy = Py.Import("numpy");
            using PyObject array = numpy.asarray(value);
            using PyObject flattened = array.InvokeMethod("ravel");
            using PyList list = new PyList(flattened.InvokeMethod("tolist"));
            double[] dbl = new double[list.Length()];
            for (int i = 0; i < dbl.Length; i++) dbl[i] = list[i].As<double>();
            return dbl;
        }
    }

    public static float[] ToFloatArray(PyObject? value)
    {
        using (Py.GIL())
        {
            if (value is null || value.IsNone())
                return [];

            dynamic numpy = Py.Import("numpy");
            using PyObject array = numpy.asarray(value);
            using PyObject flattened = array.InvokeMethod("ravel");
            using PyList list = new PyList(flattened.InvokeMethod("tolist"));
            var values = new float[list.Length()];
            for (int index = 0; index < values.Length; index++)
                values[index] = list[index].As<float>();
            return values;
        }
    }

    public static (EmbeddingValueKind Kind, float[] Values, Dictionary<int, string> Categories) ToEmbeddingArray(PyObject? value)
    {
        using (Py.GIL())
        {
            if (value is null || value.IsNone())
                return (EmbeddingValueKind.Float, [], new Dictionary<int, string>());

            dynamic numpy = Py.Import("numpy");
            using PyObject array = numpy.asarray(value);
            using PyObject flattened = array.InvokeMethod("ravel");
            using PyObject dtype = array.GetAttr("dtype");
            string dtype_kind = dtype.GetAttr("kind").As<string>();

            if (dtype_kind is "U" or "S")
                return strings_to_categories(new PyList(flattened.InvokeMethod("tolist")));

            if (dtype_kind == "O")
            {
                using var builtins = Py.Import("builtins");
                using var str_type = builtins.GetAttr("str");
                using PyList object_list = new PyList(flattened.InvokeMethod("tolist"));
                bool all_strings = true;
                foreach (PyObject item in object_list)
                {
                    using var is_string = builtins.InvokeMethod("isinstance", item, str_type);
                    if (is_string.As<bool>())
                        continue;
                    all_strings = false;
                    break;
                }

                if (all_strings)
                    return strings_to_categories(object_list);
            }

            if (dtype_kind is not ("f" or "i" or "u"))
                throw new ArgumentException("Embedding values must be a NumPy array of floats or strings.");

            using PyList list = new PyList(flattened.InvokeMethod("tolist"));
            var values = new float[list.Length()];
            for (int index = 0; index < values.Length; index++)
                values[index] = list[index].As<float>();
            return (EmbeddingValueKind.Float, values, new Dictionary<int, string>());
        }
    }

    private static (EmbeddingValueKind Kind, float[] Values, Dictionary<int, string> Categories) strings_to_categories(PyList list)
    {
        var ids_by_label = new Dictionary<string, int>(StringComparer.Ordinal);
        var categories = new Dictionary<int, string>();
        var values = new float[list.Length()];
        for (int index = 0; index < values.Length; index++)
        {
            string label = list[index].As<string>();
            if (!ids_by_label.TryGetValue(label, out int id))
            {
                id = ids_by_label.Count + 1;
                ids_by_label[label] = id;
                categories[id] = label;
            }

            values[index] = id;
        }

        return (EmbeddingValueKind.Integer, values, categories);
    }

    public static List<T> To<T>(PyList value)
    {
        using (Py.GIL())
        {
            List<T> values = new();
            foreach(PyObject? obj in value)
                values.Add(obj.As<T>());
            return values;
        }
    }

    public static float[,] SelectRows(float[,] matrix, IReadOnlyList<int> rows)
    {
        int column_count = matrix.GetLength(1);
        var selected = new float[rows.Count, column_count];
        for (int row = 0; row < rows.Count; row++)
        {
            int source_row = rows[row];
            if (source_row < 0 || source_row >= matrix.GetLength(0))
                continue;
            for (int column = 0; column < column_count; column++)
                selected[row, column] = matrix[source_row, column];
        }

        return selected;
    }
}
