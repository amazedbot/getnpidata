#!/bin/bash
# Runs once, when the MySQL container starts with an empty data volume (docker-entrypoint-initdb.d). The image already
# created the database and npi_loader (every privilege on it); this adds the rest of the logins in CLAUDE.md §8.
set -euo pipefail
mysql -uroot -p"${MYSQL_ROOT_PASSWORD}" <<SQL
-- The loader creates the 330-column npidata table with innodb_strict_mode off.
GRANT SESSION_VARIABLES_ADMIN ON *.* TO 'npi_loader'@'%';
-- The site reads only.
CREATE USER IF NOT EXISTS 'npi_web'@'%' IDENTIFIED BY '${NPI_WEB_PASSWORD}';
GRANT SELECT ON \`${MYSQL_DATABASE}\`.* TO 'npi_web'@'%';
-- The MySQL tests create and drop their own npi_test_it_* databases.
CREATE USER IF NOT EXISTS 'npi_test'@'%' IDENTIFIED BY '${NPI_TEST_PASSWORD}';
GRANT ALL PRIVILEGES ON \`npi\_test\_%\`.* TO 'npi_test'@'%';
GRANT SESSION_VARIABLES_ADMIN ON *.* TO 'npi_test'@'%';
SQL
