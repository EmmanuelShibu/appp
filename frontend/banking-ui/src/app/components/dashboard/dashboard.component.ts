// src/app/components/dashboard/dashboard.component.ts
import { Component, OnInit } from '@angular/core';
import { CommonModule }      from '@angular/common';
import { Router, RouterModule } from '@angular/router';
import { BankingService }    from '../../services/banking.service';
import { Account, Transaction } from '../../models/banking.models';
import { catchError, of }    from 'rxjs';

interface Alert {
  type: 'success' | 'warning' | 'error';
  message: string;
}

@Component({
  selector: 'app-dashboard',
  standalone: true,
  imports: [CommonModule, RouterModule],
  templateUrl: './dashboard.component.html',
  styleUrls: ['./dashboard.component.scss']
})
export class DashboardComponent implements OnInit {

  account: Account | null = null;
  transactions: Transaction[] = [];
  loading        = true;
  txLoading      = true;
  chaosLoading   = '';
  alert: Alert | null = null;

  constructor(
    private svc:    BankingService,
    private router: Router
  ) {}

  ngOnInit(): void {
    const raw = sessionStorage.getItem('currentAccount');
    if (!raw) { this.router.navigate(['/login']); return; }

    this.account = JSON.parse(raw) as Account;
    this.loadFreshBalance();
    this.loadTransactions();
  }

  loadFreshBalance(): void {
    this.loading = true;
    this.svc.getAccount(this.account!.accountNumber)
      .pipe(catchError(() => of(this.account!)))
      .subscribe(acc => {
        this.account = acc;
        sessionStorage.setItem('currentAccount', JSON.stringify(acc));
        this.loading = false;
      });
  }

  loadTransactions(): void {
    this.txLoading = true;
    this.svc.getTransactions(this.account!.accountNumber)
      .pipe(catchError(() => of([] as Transaction[])))
      .subscribe(txns => {
        this.transactions = txns;
        this.txLoading    = false;
      });
  }

  // ── Chaos ─────────────────────────────────────────────────
  triggerChaos(type: 'null' | 'db' | 'unhandled'): void {
    this.chaosLoading = type;
    this.alert = null;

    const obs$ =
      type === 'null'      ? this.svc.chaosNullReference() :
      type === 'db'        ? this.svc.chaosDbTimeout()     :
                             this.svc.chaosUnhandled();

    obs$.pipe(
      catchError(err => {
        this.alert = {
          type: 'error',
          message: `ERROR logged ✓ — ${err.error?.message ?? err.message ?? 'Server returned an error'}`
        };
        this.chaosLoading = '';
        return of(null);
      })
    ).subscribe(res => {
      if (res !== null) {
        this.alert = { type: 'error', message: 'ERROR endpoint hit successfully – check log file.' };
      }
      this.chaosLoading = '';
    });
  }

  // ── Helpers ───────────────────────────────────────────────
  statusClass(status: string): string {
    return {
      'SUCCESS': 'badge-success',
      'WARNING': 'badge-warn',
      'FAILED' : 'badge-error'
    }[status] ?? 'badge-dim';
  }

  dismissAlert(): void { this.alert = null; }
}
