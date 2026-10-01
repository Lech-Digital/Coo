namespace Coo.Domain.Common;

/// <summary>
/// The outcome of an operation that can break a rule. Rule violations are values, never exceptions
/// (Spec 001 §3.2). Exceptions are reserved for programming errors.
/// </summary>
public class Result
{
    protected Result(DomainError? error) => Error = error;

    public DomainError? Error { get; }

    public bool IsSuccess => Error is null;

    public bool IsFailure => Error is not null;

    public static Result Success { get; } = new(null);

    public static Result Failure(DomainError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new Result(error);
    }

    public static Result Failure(string code) => Failure(DomainError.Of(code));

    /// <summary>Runs the checks in order and returns the first failure (Spec 001 §3.2).</summary>
    public static Result FirstFailure(params Func<Result>[] checks)
    {
        ArgumentNullException.ThrowIfNull(checks);
        foreach (var check in checks)
        {
            var result = check();
            if (result.IsFailure)
                return result;
        }

        return Success;
    }

    public static Result Check(bool condition, string code) => condition ? Success : Failure(code);

    public static implicit operator Result(DomainError error) => Failure(error);
}

/// <summary>A <see cref="Result"/> that carries a value on success.</summary>
public sealed class Result<T> : Result
{
    private readonly T? _value;

    private Result(T value)
        : base(null) => _value = value;

    private Result(DomainError error)
        : base(error)
    {
    }

    /// <summary>The value. Reading it from a failure is a programming error.</summary>
    public T Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException($"Result failed with '{Error}'; there is no value.");

#pragma warning disable CA1000 // A factory on the generic type is the clearest API here.
    public static Result<T> Ok(T value) => new(value);

    public static new Result<T> Failure(DomainError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new Result<T>(error);
    }

    public static new Result<T> Failure(string code) => Failure(DomainError.Of(code));
#pragma warning restore CA1000

    public Result<TNext> Then<TNext>(Func<T, Result<TNext>> next)
    {
        ArgumentNullException.ThrowIfNull(next);
        return IsSuccess ? next(_value!) : Result<TNext>.Failure(Error!);
    }

    public static implicit operator Result<T>(T value) => Ok(value);

    public static implicit operator Result<T>(DomainError error) => Failure(error);
}
