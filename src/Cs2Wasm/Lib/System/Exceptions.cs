#pragma warning disable CS0436, CS0114
using Cs2Wasm.Runtime;

namespace System
{
    public class Exception
    {
        public new Type GetType() => base.GetType();
        internal string _message;
        Exception _innerException;
        internal string _stackTrace;
        internal string _fullTrace;
        internal int _throwDepth;
        int _hresult = -2146233088;

        public Exception() { }
        public Exception(string message) { _message = message; }
        public Exception(string message, Exception innerException) { _message = message; _innerException = innerException; }

        public virtual string Message => _message ?? "Exception of type '" + Rt.TypeName(this) + "' was thrown.";
        public Exception InnerException => _innerException;
        public virtual string StackTrace => _stackTrace;
        public virtual string Source { get; set; }
        public int HResult { get => _hresult; set => _hresult = value; }
        public virtual System.Collections.IDictionary Data => null;
        public virtual Exception GetBaseException()
        {
            Exception e = this;
            while (e._innerException != null) e = e._innerException;
            return e;
        }

        public override string ToString()
        {
            string s = Rt.TypeName(this);
            string m = Message;
            if (m != null && m.Length > 0) s = s + ": " + m;
            if (_innerException != null)
                s = s + "\n ---> " + _innerException.ToString() + "\n   --- End of inner exception stack trace ---";
            string st = StackTrace;
            if (st != null) s = s + "\n" + st;
            return s;
        }
    }

    public class SystemException : Exception
    {
        public SystemException() : base("System error.") { }
        public SystemException(string message) : base(message) { }
        public SystemException(string message, Exception innerException) : base(message, innerException) { }
    }

    public class ApplicationException : Exception
    {
        public ApplicationException() : base("Error in the application.") { }
        public ApplicationException(string message) : base(message) { }
        public ApplicationException(string message, Exception innerException) : base(message, innerException) { }
    }

    public class ArgumentException : SystemException
    {
        readonly string _paramName;
        public ArgumentException() : base("Value does not fall within the expected range.") { }
        public ArgumentException(string message) : base(message) { }
        public ArgumentException(string message, Exception innerException) : base(message, innerException) { }
        public ArgumentException(string message, string paramName) : base(message) { _paramName = paramName; }
        public ArgumentException(string message, string paramName, Exception innerException) : base(message, innerException) { _paramName = paramName; }
        public virtual string ParamName => _paramName;
        public override string Message
        {
            get
            {
                string m = base.Message;
                if (_paramName != null && _paramName.Length > 0) return m + " (Parameter '" + _paramName + "')";
                return m;
            }
        }
        public static void ThrowIfNullOrEmpty(string argument, string paramName = null)
        {
            if (argument == null) throw new ArgumentNullException(paramName);
            if (argument.Length == 0) throw new ArgumentException("The value cannot be an empty string.", paramName);
        }
    }

    public class ArgumentNullException : ArgumentException
    {
        public ArgumentNullException() : base("Value cannot be null.") { }
        public ArgumentNullException(string paramName) : base("Value cannot be null.", paramName) { }
        public ArgumentNullException(string paramName, string message) : base(message, paramName) { }
        public ArgumentNullException(string message, Exception innerException) : base(message, innerException) { }
        public static void ThrowIfNull(object argument, string paramName = null)
        {
            if (argument == null) throw new ArgumentNullException(paramName);
        }
    }

    public class ArgumentOutOfRangeException : ArgumentException
    {
        readonly object _actualValue;
        public ArgumentOutOfRangeException() : base("Specified argument was out of the range of valid values.") { }
        public ArgumentOutOfRangeException(string paramName) : base("Specified argument was out of the range of valid values.", paramName) { }
        public ArgumentOutOfRangeException(string paramName, string message) : base(message, paramName) { }
        public ArgumentOutOfRangeException(string paramName, object actualValue, string message) : base(message, paramName) { _actualValue = actualValue; }
        public ArgumentOutOfRangeException(string message, Exception innerException) : base(message, innerException) { }
        public virtual object ActualValue => _actualValue;
        public override string Message
        {
            get
            {
                string m = base.Message;
                if (_actualValue != null) return m + "\nActual value was " + _actualValue.ToString() + ".";
                return m;
            }
        }
        public static void ThrowIfNegative(int value, string paramName = null)
        {
            if (value < 0) throw new ArgumentOutOfRangeException(paramName, value, paramName + " ('" + value.ToString() + "') must be a non-negative value.");
        }
    }

    public class ArithmeticException : SystemException
    {
        public ArithmeticException() : base("Overflow or underflow in the arithmetic operation.") { }
        public ArithmeticException(string message) : base(message) { }
        public ArithmeticException(string message, Exception innerException) : base(message, innerException) { }
    }

    public class DivideByZeroException : ArithmeticException
    {
        public DivideByZeroException() : base("Attempted to divide by zero.") { }
        public DivideByZeroException(string message) : base(message) { }
        public DivideByZeroException(string message, Exception innerException) : base(message, innerException) { }
    }

    public class OverflowException : ArithmeticException
    {
        public OverflowException() : base("Arithmetic operation resulted in an overflow.") { }
        public OverflowException(string message) : base(message) { }
        public OverflowException(string message, Exception innerException) : base(message, innerException) { }
    }

    public class NotFiniteNumberException : ArithmeticException
    {
        public NotFiniteNumberException() : base("Number encountered was not a finite quantity.") { }
        public NotFiniteNumberException(string message) : base(message) { }
    }

    public class FormatException : SystemException
    {
        public FormatException() : base("One of the identified items was in an invalid format.") { }
        public FormatException(string message) : base(message) { }
        public FormatException(string message, Exception innerException) : base(message, innerException) { }
    }

    public class IndexOutOfRangeException : SystemException
    {
        public IndexOutOfRangeException() : base("Index was outside the bounds of the array.") { }
        public IndexOutOfRangeException(string message) : base(message) { }
        public IndexOutOfRangeException(string message, Exception innerException) : base(message, innerException) { }
    }

    public class InvalidCastException : SystemException
    {
        public InvalidCastException() : base("Specified cast is not valid.") { }
        public InvalidCastException(string message) : base(message) { }
        public InvalidCastException(string message, Exception innerException) : base(message, innerException) { }
    }

    public class InvalidOperationException : SystemException
    {
        public InvalidOperationException() : base("Operation is not valid due to the current state of the object.") { }
        public InvalidOperationException(string message) : base(message) { }
        public InvalidOperationException(string message, Exception innerException) : base(message, innerException) { }
    }

    public class ObjectDisposedException : InvalidOperationException
    {
        readonly string _objectName;
        public ObjectDisposedException(string objectName) : base("Cannot access a disposed object.") { _objectName = objectName; }
        public ObjectDisposedException(string objectName, string message) : base(message) { _objectName = objectName; }
        public string ObjectName => _objectName ?? "";
        public override string Message
        {
            get
            {
                string n = ObjectName;
                if (n.Length == 0) return base.Message;
                return base.Message + "\nObject name: '" + n + "'.";
            }
        }
    }

    public class NotSupportedException : SystemException
    {
        public NotSupportedException() : base("Specified method is not supported.") { }
        public NotSupportedException(string message) : base(message) { }
        public NotSupportedException(string message, Exception innerException) : base(message, innerException) { }
    }

    public class PlatformNotSupportedException : NotSupportedException
    {
        public PlatformNotSupportedException() : base("Operation is not supported on this platform.") { }
        public PlatformNotSupportedException(string message) : base(message) { }
    }

    public class NotImplementedException : SystemException
    {
        public NotImplementedException() : base("The method or operation is not implemented.") { }
        public NotImplementedException(string message) : base(message) { }
        public NotImplementedException(string message, Exception inner) : base(message, inner) { }
    }

    public sealed class NullReferenceException : SystemException
    {
        public NullReferenceException() : base("Object reference not set to an instance of an object.") { }
        public NullReferenceException(string message) : base(message) { }
        public NullReferenceException(string message, Exception innerException) : base(message, innerException) { }
    }

    public sealed class OutOfMemoryException : SystemException
    {
        public OutOfMemoryException() : base("Insufficient memory to continue the execution of the program.") { }
        public OutOfMemoryException(string message) : base(message) { }
    }

    public sealed class ArrayTypeMismatchException : SystemException
    {
        public ArrayTypeMismatchException() : base("Attempted to access an element as a type incompatible with the array.") { }
        public ArrayTypeMismatchException(string message) : base(message) { }
    }

    public sealed class StackOverflowException : SystemException
    {
        public StackOverflowException() : base("Operation caused a stack overflow.") { }
    }

    public class TimeoutException : SystemException
    {
        public TimeoutException() : base("The operation has timed out.") { }
        public TimeoutException(string message) : base(message) { }
    }

    public class RankException : SystemException
    {
        public RankException() : base("Attempted to operate on an array with the incorrect number of dimensions.") { }
    }

    public class OperationCanceledException : SystemException
    {
        public OperationCanceledException() : base("The operation was canceled.") { }
        public OperationCanceledException(string message) : base(message) { }
    }
}

namespace System.Collections.Generic
{
    public class KeyNotFoundException : SystemException
    {
        public KeyNotFoundException() : base("The given key was not present in the dictionary.") { }
        public KeyNotFoundException(string message) : base(message) { }
        public KeyNotFoundException(string message, Exception innerException) : base(message, innerException) { }
    }
}
