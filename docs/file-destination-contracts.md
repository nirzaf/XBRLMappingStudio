# Versioned file destination contracts

File destinations are selected by the DestinationPlan.Format value. New CSV and
XLSX destinations use csv-records-v2 and xlsx-records-v2. Existing
csv-records-v1 and xlsx-records-v1 projects remain valid and keep their existing
empty-cell behavior. JSON and XML continue to use their *-records-v1 contracts.

## CSV and XLSX v2 field presence

The data sheet/table includes an additional __xms_field_presence column. Each
row stores a JSON document with this shape:

~~~json
{
  "schema": "xms-field-presence-v1",
  "fields": [
    { "name": "concept", "state": "present" },
    { "name": "instant_date", "state": "nil" }
  ]
}
~~~

The fields array follows the destination data-column order and covers every
regular output column. Each state has a precise meaning:

- missing: the record had no value for the field.
- nil: the record explicitly contained a nil value.
- blank: the record contained a blank string.
- present: the record contained a nonblank value, including numeric zero.

The ordinary CSV/XLSX cells remain convenient for spreadsheet use. Consumers that
need to distinguish empty-cell states should read the v2 presence map. The reserved
metadata column is part of the versioned contract.
