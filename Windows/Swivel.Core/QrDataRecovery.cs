using System;
using System.Threading;

namespace Swivel.Core;

public sealed record QrDataRecoveryResult(QrReadResult Result,int CorrectedCodewords,int CorrectedBlocks);

// Original bounded algebra for QR's GF(256), polynomial 0x11d, generator roots alpha^0 onward.
// No erasures, function-module repair, payload guessing or external implementation is used.
internal static class QrDataRecovery
{
    private static readonly byte[] powers = MakePowers();
    private static readonly byte[] logarithms = MakeLogs();
    private static byte[] MakePowers()
    {
        var result=new byte[510];int value=1;
        for(int i=0;i<255;i++){result[i]=(byte)value;value<<=1;if((value&256)!=0)value^=0x11d;}
        for(int i=255;i<result.Length;i++)result[i]=result[i-255];return result;
    }
    private static byte[] MakeLogs()
    {var result=new byte[256];for(int i=0;i<255;i++)result[powers[i]]=(byte)i;return result;}
    private static byte Product(byte a,byte b) => a==0||b==0?(byte)0:powers[logarithms[a]+logarithms[b]];
    private static byte Quotient(byte a,byte b)
    {if(b==0)throw new InvalidOperationException("A zero finite-field divisor was refused.");return a==0?(byte)0:powers[logarithms[a]+255-logarithms[b]];}
    private static byte Evaluate(ReadOnlySpan<byte> coefficients,byte x)
    {byte value=0;for(int i=coefficients.Length-1;i>=0;i--)value=(byte)(Product(value,x)^coefficients[i]);return value;}
    private static byte[] Syndromes(byte[] words,int parity,CancellationToken token)
    {
        var result=new byte[parity];
        for(int j=0;j<parity;j++)
        {
            token.ThrowIfCancellationRequested();byte x=powers[j],value=0;
            foreach(byte word in words)value=(byte)(Product(value,x)^word);
            result[j]=value;
        }
        return result;
    }
    private static bool IsZero(byte[] values)
    {foreach(byte value in values)if(value!=0)return false;return true;}

    // Input is immutable. On refusal or cancellation no partially corrected block is returned.
    internal static bool TryRepair(byte[] received,int parity,CancellationToken token,out byte[]? repaired,out int changed)
    {
        repaired=null;changed=0;token.ThrowIfCancellationRequested();
        if(received==null||parity is <1 or >30||received.Length<=parity||received.Length>255)throw new ArgumentException("Invalid QR data block.");
        var syndromes=Syndromes(received,parity,token);
        if(IsZero(syndromes)){token.ThrowIfCancellationRequested();repaired=(byte[])received.Clone();return true;}

        // Berlekamp-Massey finds C(z) = product(1 + X_i*z) from S_0 ... S_(p-1).
        // S_0 alone can vanish for damaged blocks and is never used as a clean-block test.
        var locator=new byte[parity+1];var prior=new byte[parity+1];locator[0]=prior[0]=1;
        int degree=0,shift=1;byte lastDiscrepancy=1;
        for(int step=0;step<parity;step++)
        {
            token.ThrowIfCancellationRequested();byte discrepancy=syndromes[step];
            for(int i=1;i<=degree;i++)discrepancy^=Product(locator[i],syndromes[step-i]);
            if(discrepancy==0){shift++;continue;}
            var before=(byte[])locator.Clone();byte factor=Quotient(discrepancy,lastDiscrepancy);
            for(int i=0;i+shift<locator.Length;i++)locator[i+shift]^=Product(factor,prior[i]);
            if(2*degree<=step){degree=step+1-degree;prior=before;lastDiscrepancy=discrepancy;shift=1;}else shift++;
        }
        if(degree==0||degree>parity/2||locator[degree]==0)return false;

        // Search only actual shortened-block positions. A byte at index i has X=alpha^(n-1-i).
        // A locator root at any absent leading position cannot be accepted as a received error.
        var positions=new int[degree];var locations=new byte[degree];int found=0;
        for(int i=0;i<received.Length;i++)
        {
            token.ThrowIfCancellationRequested();int exponent=received.Length-1-i;
            byte inverse=powers[(255-exponent)%255];
            if(Evaluate(locator.AsSpan(0,degree+1),inverse)!=0)continue;
            if(found==degree)return false;positions[found]=i;locations[found]=powers[exponent];found++;
        }
        if(found!=degree)return false;

        // Direct GF Vandermonde solve avoids an error-evaluator/Forney index convention.
        // Row j is S_j = sum(Y_i * X_i^j), including the all-ones j=0 row.
        var matrix=new byte[degree,degree+1];
        for(int column=0;column<degree;column++)
        {byte value=1;for(int row=0;row<degree;row++){matrix[row,column]=value;value=Product(value,locations[column]);}}
        for(int row=0;row<degree;row++)matrix[row,degree]=syndromes[row];
        for(int column=0;column<degree;column++)
        {
            token.ThrowIfCancellationRequested();int pivot=column;
            while(pivot<degree&&matrix[pivot,column]==0)pivot++;
            if(pivot==degree)return false;
            if(pivot!=column)for(int j=0;j<=degree;j++)(matrix[column,j],matrix[pivot,j])=(matrix[pivot,j],matrix[column,j]);
            byte divisor=matrix[column,column];for(int j=column;j<=degree;j++)matrix[column,j]=Quotient(matrix[column,j],divisor);
            for(int row=0;row<degree;row++)
            {
                token.ThrowIfCancellationRequested();if(row==column)continue;byte factor=matrix[row,column];
                for(int j=column;j<=degree;j++)matrix[row,j]^=Product(factor,matrix[column,j]);
            }
        }
        var candidate=(byte[])received.Clone();
        for(int i=0;i<degree;i++)
        {byte magnitude=matrix[i,degree];if(magnitude==0)return false;candidate[positions[i]]^=magnitude;}
        if(!IsZero(Syndromes(candidate,parity,token)))return false;
        int differences=0;for(int i=0;i<candidate.Length;i++)if(candidate[i]!=received[i])differences++;
        if(differences!=degree)return false;
        token.ThrowIfCancellationRequested();repaired=candidate;changed=differences;return true;
    }
}
