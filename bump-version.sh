#!/bin/sh
# Sets the BUILD file to today's version, shared by both apps: 26.10.30, then 26.10.30 V2, V3... for more that day.
cd "$(dirname "$0")"
TODAY=$(date +%y.%m.%d)
CUR=$(cat BUILD)
case "$CUR" in
  "$TODAY") NEW="$TODAY V2" ;;
  "$TODAY V"*) NEW="$TODAY V$((${CUR##*V} + 1))" ;;
  *) NEW="$TODAY" ;;
esac
echo "$NEW" > BUILD
echo "Version $NEW"
