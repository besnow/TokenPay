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
CREATE TABLE ChainPayment (
  Id TEXT NOT NULL PRIMARY KEY, Network TEXT NOT NULL, Asset TEXT NOT NULL,
  TokenContract TEXT NULL, TransactionHash TEXT NOT NULL, TransferIndex INTEGER NOT NULL,
  FromAddress TEXT NULL, ToAddress TEXT NOT NULL, ActualAmount DECIMAL(38,18) NOT NULL,
  BlockNumber INTEGER NOT NULL, BlockTime TEXT NOT NULL, Confirmations INTEGER NOT NULL,
  FirstSeenTime TEXT NOT NULL, MatchStatus INTEGER NOT NULL, MatchedOrderId TEXT NULL,
  MatchMethod INTEGER NULL, MatchReason TEXT NULL
);
CREATE UNIQUE INDEX uk_chain_payment ON ChainPayment(Network, TransactionHash, TransferIndex);
COMMIT;
