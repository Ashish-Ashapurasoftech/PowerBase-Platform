# POC QuickBase test app: spec

The QuickBase app the team builds for Stage 1 of the QuickBase → PowerBase migration (see the plan's "Stage 1 — POC"). Each item below covers a migration risk. Please build it exactly as written, because field types, keys and edge cases are what we're testing.

**App name:** `ZZ QB Migration POC`
**Size:** about 8,500 records across 5 tables.
**User token:** a dedicated token used only for this app (stored later in the migration Key Vault).

## Tables and relationships

| Table | Parent of | Key field | Why |
|---|---|---|---|
| Regions | Customers | **Region Code** (Text, custom key) | Tests a relationship whose parent key is **not** Record ID#, so the reference translation step is needed |
| Products | Order Lines | Record ID# | A root table |
| Customers | Orders, Customers (self) | Record ID# | Self-reference, **Parent Customer**, tests cycles in the tier order |
| Orders | Order Lines | Record ID# | Middle tier |
| Order Lines | none | Record ID# | Two parents (Orders and Products) |

## Fields

### Regions (5 records)
| Field | QB type | Notes |
|---|---|---|
| Region Code | Text | Set as the table's **key field**; values like `NA`, `EU`, `APAC`, `LATAM`, `MEA` |
| Region Name | Text | |

### Products (50 records)
| Field | QB type | Notes |
|---|---|---|
| Product Name | Text | |
| SKU | Text | Unique |
| Unit Price | Currency | Include `0.0001`, `999999.9999` |
| In Stock | Checkbox | |
| Rating | Rating | |

### Customers (500 records)
| Field | QB type | Notes |
|---|---|---|
| Customer Name | Text | Include Unicode (`Zürich GmbH`, `東京商事`) and an emoji |
| Notes | Text - Multi-line | One value over 5,000 characters |
| Profile | Rich Text | HTML with a link and a table |
| Segment | Text - Multiple Choice | Single choice |
| Tags | Multi-select Text | Include a choice containing a comma |
| Email | Email | |
| Phone | Phone | |
| Website | URL | |
| Address | Address | Some full, some partial (city only), some blank |
| Is Active | Checkbox | |
| Account Manager | User | Use **3 users**: one also in PowerBase, one not in PowerBase, one deactivated in QB |
| Watchers | List of Users | 0, 1 and 3 users |
| Region | Reference to Regions | Through **Region Code**; leave about 10 blank |
| Parent Customer | Reference to Customers | About 30 set; include one A→B→A loop |
| Credit Note | Text | **Mark encrypted in PowerBase** after the QBL import, to test encryption |
| Total Orders | Summary (count of Orders) | Computed; checked against QB in Gate 6 |
| Total Order Value | Summary (sum of Orders.Order Total) | Computed |

### Orders (2,000 records)
| Field | QB type | Notes |
|---|---|---|
| Customer | Reference to Customers | |
| Customer Name | Lookup (Customers.Customer Name) | Computed |
| Order Date | Date | Include `1900-01-01` and `2099-12-31` |
| Shipped At | Date / Time | Include blanks |
| Delivery Window | Time of Day | |
| Discount | Percent | Values `0%`, `12.5%`, `150%` (QB returns `.125` and `1.5`) |
| Handling Time | Duration | `0`, `90 minutes`, `3 days` (QB returns milliseconds) |
| Priority | Numeric | Include negative and 4-plus decimal places |
| Status | Text - Multiple Choice | |
| Order Total | Summary (sum of Order Lines.Line Total) | Computed |
| Is Large | Formula - checkbox | `[Order Total] > 1000` |

### Order Lines (6,000 records)
| Field | QB type | Notes |
|---|---|---|
| Order | Reference to Orders | |
| Product | Reference to Products | |
| Quantity | Numeric | |
| Unit Price | Lookup (Products.Unit Price) | Computed |
| Line Total | Formula - numeric | `[Quantity] * [Unit Price]` |

## Edge cases to create after seeding
1. **Gaps in Record IDs:** delete about 5% of Customers and Orders records, including a block of 200 consecutive Order IDs. This tests range chunks with sparse IDs.
2. **Orphans:** two Order Lines whose Order was deleted. They should show in Gate 4 as known issues, not as migration errors.
3. **Edited records:** edit 20 Customers after seeding so Date Modified differs from Date Created.
4. **Records owned by several users:** create some records as each of the 3 users, so Record Owner and Last Modified By differ.
5. **Blanks:** every non-required field blank on at least one record.

## What we need back
- The QB realm hostname and app id (dbid).
- The QBL export of the app.
- The dedicated user token, sent through a secure channel and never by email or chat.
- The emails of the 3 test users.

## How the POC uses it
1. Import the QBL into a new scratch app on the dev tenant.
2. Map QB tables and fields to PB.
3. Create the missing users as inactive, with no emails.
4. Load all 5 tables with 2 workers.
5. Run the validation gates with a 100% field comparison.
6. Run the load again: results must be identical, with no duplicates.
7. Run the failure drills.
