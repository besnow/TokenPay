-- Run with the application stopped. The CHECK aborts the transaction rather than
-- silently merging one chain event that was already assigned to different orders.
BEGIN IMMEDIATE;
CREATE TEMP TABLE CanonicalHashConflictGuard(Value INTEGER CHECK(Value = 0));
INSERT INTO CanonicalHashConflictGuard
SELECT COUNT(*) FROM (
  SELECT Network,
         CASE WHEN upper(Network) = 'TRON'
              THEN upper(replace(TransactionHash, '0x', ''))
              ELSE '0x' || lower(replace(TransactionHash, '0x', '')) END AS CanonicalHash,
         TransferKey
  FROM ChainPayment
  WHERE MatchedOrderId IS NOT NULL
  GROUP BY Network, CanonicalHash, TransferKey
  HAVING COUNT(DISTINCT MatchedOrderId) > 1
);

-- The application migration consolidates harmless unbound/same-order duplicates
-- while updating PaymentClaim and TokenOrders foreign keys, then updates hashes.
-- It deliberately performs that relational work in one FreeSql transaction.
DROP TABLE CanonicalHashConflictGuard;
COMMIT;
