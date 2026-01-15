json_file="$(dirname "$0")/env.config.json"
json="{"
for var in $(printenv)
do
    # Extract the variable name and value
    var_name=$(echo "$var" | cut -d '=' -f 1)
    var_value=$(echo "$var" | cut -d '=' -f 2-)
       
    if grep -q $var_name $json_file;
    then
        eval envVar='$'$var
        envObj="$envObj'$var': '${envVar:=''}',"
        # Escape double quotes and backslashes in the value
        var_value=$(echo "$var_value" | sed 's/\\/\\\\/g' | sed 's/"/\\"/g')

        # Append to JSON object
        json="$json\"$var_name\":\"$var_value\","
    else
      echo "$var_name is NOT a valid env for this service"
    fi
done
# Remove the trailing comma
json=$(echo "$json" | sed 's/,$//')

# Close the JSON object
json="$json}"

# Write the JSON object to env.config.json
echo "$json" > /env-scripts/env.config.json
cat /env-scripts/env.config.json #print env to container log
exec "$@"
