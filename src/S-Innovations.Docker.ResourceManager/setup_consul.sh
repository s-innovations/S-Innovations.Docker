#! /bin/bash
timeout=300 #seconds
now=$(date +%s)
let deadline=now+timeout 
log=/var/log/azure/Microsoft.Azure.Extensions.DockerExtension/2.1.5/extension.log

while ( [ ! -f "$log" ] || (! grep "Start" $log) )
do
  if [ $(date +%s) -gt "$deadline" ]
  then
    echo "Timeout"
    break
  fi
  echo "Sleeping"
  sleep 3
done

echo "Done waiting"