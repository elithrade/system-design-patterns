#!/bin/bash
# Runs once, when the primary's data directory is first created.
# Adds a login the replica uses to stream WAL, and lets it connect for replication.
set -e
psql -v ON_ERROR_STOP=1 -U postgres -d postgres -c "CREATE ROLE replicator WITH REPLICATION LOGIN PASSWORD 'replicator';"
echo "host replication replicator all scram-sha-256" >> "$PGDATA/pg_hba.conf"
