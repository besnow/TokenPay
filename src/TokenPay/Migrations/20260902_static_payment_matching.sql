-- Reference migration for installations that manage schema outside FreeSql CodeFirst.
-- The application also applies these additive changes through UseAutoSyncStructure.
BEGIN;
ALTER TABLE TokenOrders ADD COLUMN LockedCoinPrice DECIMAL(38,18) NOT NULL DEFAULT 0;
ALTER TABLE TokenOrders ADD COLUMN OrderValueUsdt DECIMAL(38,18) NOT NULL DEFAULT 0;
ALTER TABLE TokenOrders ADD COLUMN AllowedUnderpayAmount DECIMAL(38,18) NOT NULL DEFAULT 0;
ALTER TABLE TokenOrders ADD COLUMN MinimumPaidAmount DECIMAL(38,18) NOT NULL DEFAULT 0;
ALTER TABLE TokenOrders ADD COLUMN IsStaticAddress INTEGER NOT NULL DEFAULT 0;
ALTER TABLE TokenOrders ADD COLUMN ChainPaymentId TEXT NULL;
ALTER TABLE TokenOrders ADD COLUMN IsLatePayment INTEGER NOT NULL DEFAULT 0;
ALTER TABLE TokenOrders ADD COLUMN PaymentMatchStatus INTEGER NOT NULL DEFAULT 0;
ALTER TABLE TokenOrders ADD COLUMN PaymentMatchReason TEXT NULL;
ALTER TABLE TokenOrders ADD COLUMN PaymentReportedAtUtc TEXT NULL;
ALTER TABLE TokenOrders ADD COLUMN MatchMethod INTEGER NULL;
CREATE TABLE ChainPayment (
  Id TEXT NOT NULL PRIMARY KEY, Network TEXT NOT NULL, Asset TEXT NOT NULL,
  TokenContract TEXT NULL, TransactionHash TEXT NOT NULL, TransferIndex INTEGER NOT NULL,
  TransferKey TEXT NOT NULL,
  FromAddress TEXT NULL, ToAddress TEXT NOT NULL, ActualAmount DECIMAL(38,18) NOT NULL,
  BlockNumber INTEGER NOT NULL, BlockTime TEXT NOT NULL, Confirmations INTEGER NOT NULL,
  FirstSeenTime TEXT NOT NULL, MatchStatus INTEGER NOT NULL, MatchedOrderId TEXT NULL,
  MatchMethod INTEGER NULL, MatchReason TEXT NULL
);
CREATE UNIQUE INDEX uk_chain_payment_key ON ChainPayment(Network, TransactionHash, TransferKey);
CREATE TABLE PaymentClaim (
  Id TEXT NOT NULL PRIMARY KEY, OrderId TEXT NOT NULL, ChainPaymentId TEXT NULL,
  Network TEXT NOT NULL, TransactionHash TEXT NOT NULL, SubmittedAtUtc TEXT NOT NULL,
  ClientIp TEXT NULL, ReviewStatus INTEGER NOT NULL, ReviewReason TEXT NULL,
  EligibleOrderIds TEXT NULL, ReviewedAtUtc TEXT NULL, ReviewedBy TEXT NULL
);
CREATE UNIQUE INDEX uk_payment_claim ON PaymentClaim(OrderId, Network, TransactionHash);
CREATE TABLE ChainScanCursor (
  Id TEXT NOT NULL PRIMARY KEY, Network TEXT NOT NULL, Asset TEXT NOT NULL, Address TEXT NOT NULL,
  LastBlockNumber INTEGER NOT NULL, LastBlockTimeUtc TEXT NOT NULL, ContinuationToken TEXT NULL, UpdatedAtUtc TEXT NOT NULL
);
CREATE UNIQUE INDEX uk_chain_scan_cursor ON ChainScanCursor(Network, Asset, Address);
COMMIT;

-- Application startup performs the configuration-dependent data migration when
-- UseDynamicAddress=false: only Pending legacy rows are marked static and their
-- MinimumPaidAmount is initialized from Amount. Historical rows are untouched.
