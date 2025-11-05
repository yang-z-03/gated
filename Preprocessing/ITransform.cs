using System;

namespace Gated.Preprocessing;

public interface ITransform
{
    public double Transform(double x);
    public void Transform(double[] x);
    public float Transform(float x);
    public void Transform(float[] x);
    
    public double InverseTransform(double x);
    public void InverseTransform(double[] x);
    public float InverseTransform(float x);
    public void InverseTransform(float[] x);
}

public class LinearTransform(
    double k = 1.0, double b = 0.0) : ITransform
{
    private double k64 = k;
    private double b64 = b;
    private float k32 = Convert.ToSingle(k);
    private float b32 = Convert.ToSingle(b);
    
    public double Transform(double data) => data * k64 + b64;
    public float Transform(float data) => data * k32 + b32;

    public void Transform(double[] data)
    {
        for (int i = 0; i < data.Length; i++)
            data[i] = this.Transform(data[i]);
    }
    
    public void Transform(float[] data)
    {
        for (int i = 0; i < data.Length; i++)
            data[i] = this.Transform(data[i]);
    }
    
    public double InverseTransform(double data) => (data - b64) / k64;
    public float InverseTransform(float data) => (data - b32) / k32;

    public void InverseTransform(double[] data)
    {
        for (int i = 0; i < data.Length; i++)
            data[i] = this.InverseTransform(data[i]);
    }
    
    public void InverseTransform(float[] data)
    {
        for (int i = 0; i < data.Length; i++)
            data[i] = this.InverseTransform(data[i]);
    }
}