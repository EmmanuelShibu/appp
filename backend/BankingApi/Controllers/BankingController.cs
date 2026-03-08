// Controllers/BankingController.cs
// =============================================================
//  Endpoint map
//  ─────────────────────────────────────────────────────────────
//  GET  /api/banking/accounts                → INFO
//  GET  /api/banking/accounts/{number}       → INFO | WARNING
//  POST /api/banking/login                   → INFO | WARNING
//  POST /api/banking/transfer                → INFO | WARNING
//  GET  /api/banking/transactions/{number}   → INFO
//  GET  /api/banking/chaos/null-reference    → ERROR
//  GET  /api/banking/chaos/db-timeout        → ERROR
//  GET  /api/banking/chaos/unhandled         → ERROR (bubbles up)
// =============================================================
using BankingApi.Data;
using BankingApi.Logging;
using BankingApi.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BankingApi.Controllers;

[ApiController]
[Route("api/banking")]
public class BankingController : ControllerBase
{
    private const string ClassName = nameof(BankingController);

    private readonly BankingDbContext _db;

    public BankingController(BankingDbContext db) => _db = db;

    // ─────────────────────────────────────────────────────────
    //  GET /api/banking/accounts
    //  Returns all accounts.  Logs → INFO
    // ─────────────────────────────────────────────────────────
    [HttpGet("accounts")]
    public async Task<IActionResult> GetAccounts()
    {
        var accounts = await _db.Accounts
            .Select(a => new AccountDto(a.Id, a.AccountNumber, a.OwnerName, a.Balance))
            .ToListAsync();

        BankingLogger.Info(ClassName,
            $"All accounts retrieved successfully. Count={accounts.Count}.");

        return Ok(accounts);
    }

    // ─────────────────────────────────────────────────────────
    //  GET /api/banking/accounts/{accountNumber}
    //  Returns one account.  Logs → INFO | WARNING
    // ─────────────────────────────────────────────────────────
    [HttpGet("accounts/{accountNumber}")]
    public async Task<IActionResult> GetAccount(string accountNumber)
    {
        var account = await _db.Accounts
            .FirstOrDefaultAsync(a => a.AccountNumber == accountNumber);

        if (account is null)
        {
            BankingLogger.Warning(ClassName,
                $"Balance check failed. AccountNumber='{accountNumber}' does not exist in the database.");

            return NotFound(new ApiError($"Account '{accountNumber}' not found."));
        }

        BankingLogger.Info(ClassName,
            $"Balance check successful. AccountNumber='{account.AccountNumber}', " +
            $"Owner='{account.OwnerName}', Balance={account.Balance:C}.");

        return Ok(new AccountDto(account.Id, account.AccountNumber, account.OwnerName, account.Balance));
    }

    // ─────────────────────────────────────────────────────────
    //  GET /api/banking/transactions/{accountNumber}
    //  Returns last 20 transactions for an account. Logs → INFO
    // ─────────────────────────────────────────────────────────
    [HttpGet("transactions/{accountNumber}")]
    public async Task<IActionResult> GetTransactions(string accountNumber)
    {
        var account = await _db.Accounts
            .FirstOrDefaultAsync(a => a.AccountNumber == accountNumber);

        if (account is null)
        {
            BankingLogger.Warning(ClassName,
                $"Transaction history requested for unknown account '{accountNumber}'.");
            return NotFound(new ApiError($"Account '{accountNumber}' not found."));
        }

        var txns = await _db.Transactions
            .Where(t => t.FromAccountId == account.Id || t.ToAccountId == account.Id)
            .OrderByDescending(t => t.CreatedAt)
            .Take(20)
            .Select(t => new TransactionDto(
                t.Id,
                t.FromAccount != null ? t.FromAccount.AccountNumber : null,
                t.ToAccount   != null ? t.ToAccount.AccountNumber   : null,
                t.Amount,
                t.TransactionType,
                t.Status,
                t.Notes,
                t.CreatedAt))
            .ToListAsync();

        BankingLogger.Info(ClassName,
            $"Transaction history retrieved. AccountNumber='{accountNumber}', Records={txns.Count}.");

        return Ok(txns);
    }

    // ─────────────────────────────────────────────────────────
    //  POST /api/banking/login
    //  Demo password for every account: password123
    //  Logs → INFO (success) | WARNING (bad credentials)
    // ─────────────────────────────────────────────────────────
    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        const string DemoPassword = "password123";

        var account = await _db.Accounts
            .FirstOrDefaultAsync(a => a.AccountNumber == request.AccountNumber);

        if (account is null)
        {
            BankingLogger.Warning(ClassName,
                $"Login failed. AccountNumber='{request.AccountNumber}' not found. " +
                "Possible invalid account or brute-force attempt.");

            return Unauthorized(new LoginResponse(false, "Invalid credentials.", null));
        }

        if (request.Password != DemoPassword)
        {
            BankingLogger.Warning(ClassName,
                $"Login failed. AccountNumber='{request.AccountNumber}' provided an incorrect password. " +
                "Failed attempt has been recorded.");

            return Unauthorized(new LoginResponse(false, "Invalid credentials.", null));
        }

        BankingLogger.Info(ClassName,
            $"Login successful. AccountNumber='{account.AccountNumber}', " +
            $"Owner='{account.OwnerName}', Timestamp={DateTime.UtcNow:O}.");

        return Ok(new LoginResponse(
            true,
            "Login successful.",
            new AccountDto(account.Id, account.AccountNumber, account.OwnerName, account.Balance)));
    }

    // ─────────────────────────────────────────────────────────
    //  POST /api/banking/transfer
    //  Logs → INFO (success) | WARNING (insufficient funds)
    //       → ERROR (unexpected DB/system failure)
    // ─────────────────────────────────────────────────────────
    [HttpPost("transfer")]
    public async Task<IActionResult> Transfer([FromBody] TransferRequest request)
    {
        if (request.Amount <= 0)
        {
            BankingLogger.Warning(ClassName,
                $"Transfer rejected. Invalid amount={request.Amount}. " +
                $"FromAccount='{request.FromAccountNumber}'.");

            return BadRequest(new TransferResponse(false, "Amount must be greater than zero.", "Warning"));
        }

        var from = await _db.Accounts
            .FirstOrDefaultAsync(a => a.AccountNumber == request.FromAccountNumber);

        var to = await _db.Accounts
            .FirstOrDefaultAsync(a => a.AccountNumber == request.ToAccountNumber);

        if (from is null || to is null)
        {
            BankingLogger.Warning(ClassName,
                $"Transfer rejected. One or both accounts not found. " +
                $"From='{request.FromAccountNumber}', To='{request.ToAccountNumber}'.");

            return NotFound(new TransferResponse(false, "One or both accounts not found.", "Warning"));
        }

        // ── INSUFFICIENT FUNDS → WARNING ──────────────────────────────────
        if (from.Balance < request.Amount)
        {
            BankingLogger.Warning(ClassName,
                $"Insufficient funds. AccountNumber='{from.AccountNumber}', " +
                $"Owner='{from.OwnerName}', " +
                $"AvailableBalance={from.Balance:C}, " +
                $"RequestedAmount={request.Amount:C}. Transfer blocked.");

            await _db.Transactions.AddAsync(new Transaction
            {
                FromAccountId   = from.Id,
                ToAccountId     = to.Id,
                Amount          = request.Amount,
                TransactionType = "TRANSFER",
                Status          = "WARNING",
                Notes           = $"Insufficient funds. Available={from.Balance:C}, Requested={request.Amount:C}"
            });
            await _db.SaveChangesAsync();

            return Ok(new TransferResponse(false, "Insufficient funds.", "Warning"));
        }

        // ── SUCCESS → INFO ────────────────────────────────────────────────
        from.Balance -= request.Amount;
        to.Balance   += request.Amount;

        await _db.Transactions.AddAsync(new Transaction
        {
            FromAccountId   = from.Id,
            ToAccountId     = to.Id,
            Amount          = request.Amount,
            TransactionType = "TRANSFER",
            Status          = "SUCCESS",
            Notes           = $"Transfer of {request.Amount:C} from {from.AccountNumber} to {to.AccountNumber}."
        });

        await _db.SaveChangesAsync();

        BankingLogger.Info(ClassName,
            $"Transfer successful. " +
            $"From='{from.AccountNumber}' ({from.OwnerName}), " +
            $"To='{to.AccountNumber}' ({to.OwnerName}), " +
            $"Amount={request.Amount:C}, " +
            $"NewFromBalance={from.Balance:C}.");

        return Ok(new TransferResponse(true, $"Transfer of {request.Amount:C} completed.", "Info"));
    }

    // =============================================================
    //  CHAOS ENDPOINTS – Intentional fault generators
    // =============================================================

    // ─────────────────────────────────────────────────────────
    //  GET /api/banking/chaos/null-reference
    //  Forces a NullReferenceException.  Logs → ERROR
    // ─────────────────────────────────────────────────────────
    [HttpGet("chaos/null-reference")]
    public IActionResult ChaosNullReference()
    {
        try
        {
            string? payload = null;
            _ = payload!.Length;   // deliberate NullReferenceException
            return Ok();
        }
        catch (NullReferenceException ex)
        {
            BankingLogger.Error(ClassName,
                "CHAOS: NullReferenceException in payment processing pipeline. " +
                "The payment object was null – possible race condition or missing initialisation. " +
                $"Source: {ex.Source}",
                ex);

            return StatusCode(500, new ApiError("Internal server error (null reference)."));
        }
    }

    // ─────────────────────────────────────────────────────────
    //  GET /api/banking/chaos/db-timeout
    //  Simulates a MySQL connection timeout.  Logs → ERROR
    // ─────────────────────────────────────────────────────────
    [HttpGet("chaos/db-timeout")]
    public IActionResult ChaosDbTimeout()
    {
        try
        {
            throw new TimeoutException(
                "A connection attempt failed because the connected MySQL host did not " +
                "respond after 30 000 ms.  Connection pool exhausted. " +
                "Server=localhost;Port=3306;Database=FaultyBankingDB");
        }
        catch (TimeoutException ex)
        {
            BankingLogger.Error(ClassName,
                "CHAOS: MySQL connection timeout. Server is unresponsive or the connection " +
                $"pool is exhausted. Transaction rolled back automatically. Detail: {ex.Message}",
                ex);

            return StatusCode(503, new ApiError("Database unavailable – connection timeout."));
        }
    }

    // ─────────────────────────────────────────────────────────
    //  GET /api/banking/chaos/unhandled
    //  Lets an exception bubble up uncaught → ASP.NET 500.
    //  Serilog captures it automatically.  Logs → ERROR
    // ─────────────────────────────────────────────────────────
    [HttpGet("chaos/unhandled")]
    public IActionResult ChaosUnhandled()
    {
        BankingLogger.Error(ClassName,
            "CHAOS: About to throw an unhandled InvalidOperationException. " +
            "This simulates a catastrophic application fault with no recovery path.");

        throw new InvalidOperationException(
            "CHAOS_FAULT: Critical ledger invariant violated – " +
            "debit and credit totals are inconsistent. Manual reconciliation required.");
    }
}
